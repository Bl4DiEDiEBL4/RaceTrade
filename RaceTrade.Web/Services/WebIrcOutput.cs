using System.Collections.Concurrent;
using System.Net;
using System.Text;
using RaceTrade.Engine.Compat;
using RaceTrade.Engine.Logging;

// ImplicitUsings pulls in Microsoft.Extensions.Logging in a web project, so the bare
// name LogLevel is ambiguous. The engine's one is the only relevant type here.
using LogLevel = RaceTrade.Engine.Logging.LogLevel;

namespace RaceTrade.Web.Services;

/// <summary>
/// Web implementation of the engine's IRC output sinks, replacing the WinForms IrcLog
/// and TabbedIrcLog windows.
///
/// Both interfaces are called straight from the IRC receive thread, so every method here
/// must be non-blocking and thread-safe (the WinForms version got that via BeginInvoke;
/// the contract now puts the responsibility here). Everything below is a lock-free
/// enqueue into a concurrent structure - no rendering, no I/O.
/// </summary>
public sealed class WebIrcOutput : IIrcOutput, IChannelOutput
{
    private const int MaxLinesPerChannel = 500;

    private readonly UiLogSink _sink;

    // (site, channel) -> recent lines, and -> user list
    private readonly ConcurrentDictionary<(string Site, string Channel), ConcurrentQueue<ChatLine>> _lines = new();
    private readonly ConcurrentDictionary<(string Site, string Channel), ConcurrentDictionary<string, byte>> _users = new();

    public WebIrcOutput(UiLogSink sink) => _sink = sink;

    /// <summary>Never disposed: unlike a Form, this lives for the process.</summary>
    public bool IsDisposed => false;

    public sealed record ChatLine(DateTimeOffset At, string Text, string Html, LogLevel Level);

    // ---- IIrcOutput: the general IRC log pane -------------------------------------

    public void AppendLog(string message, Color color)
        => _sink.Write(new LogEvent
        {
            Level = color.Level,
            Channel = LogChannel.Irc,
            Message = message
        });

    // ---- IChannelOutput: per-channel chat ----------------------------------------

    public void EnsureChannel(string siteName, string channelName)
    {
        var key = Key(siteName, channelName);
        _lines.GetOrAdd(key, _ => new ConcurrentQueue<ChatLine>());
        _users.GetOrAdd(key, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
    }

    public void AppendChannelMessage(string siteName, string channelName, string message, Color color)
    {
        var q = _lines.GetOrAdd(Key(siteName, channelName), _ => new ConcurrentQueue<ChatLine>());
        q.Enqueue(new ChatLine(DateTimeOffset.Now, message, IrcMarkup.ToHtml(message), color.Level));

        // Bound the buffer without locking; a brief overshoot is fine.
        while (q.Count > MaxLinesPerChannel && q.TryDequeue(out _)) { }

        Changed?.Invoke();
    }

    public void AddUser(string siteName, string channelName, string username)
    {
        _users.GetOrAdd(Key(siteName, channelName),
            _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase))[username] = 0;
    }

    public void RemoveUser(string siteName, string channelName, string username)
    {
        if (_users.TryGetValue(Key(siteName, channelName), out var set))
            set.TryRemove(username, out _);
    }

    public void UpdateUserList(string siteName, string channelName, List<string> users)
    {
        var set = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in users ?? new List<string>())
            set[u] = 0;

        _users[Key(siteName, channelName)] = set;
    }

    // ---- read side, for the Blazor chat page --------------------------------------

    public event Action? Changed;

    public IReadOnlyList<(string Site, string Channel)> Channels =>
        _lines.Keys
            .OrderBy(k => k.Site)
            .ThenBy(k => IsPrivateMessage(k.Channel))
            .ThenBy(k => PlainChannelName(k.Channel), StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<ChatLine> Lines(string site, string channel) =>
        _lines.TryGetValue(Key(site, channel), out var q) ? q.ToArray() : Array.Empty<ChatLine>();

    public IReadOnlyList<string> Users(string site, string channel) =>
        _users.TryGetValue(Key(site, channel), out var set)
            ? set.Keys
                .OrderBy(UserRank)
                .ThenBy(PlainNick, StringComparer.OrdinalIgnoreCase)
                .ToList()
            : Array.Empty<string>();

    private static (string, string) Key(string site, string channel) =>
        (site ?? "", channel ?? "");

    private static int UserRank(string user) => RankPrefix(user) switch
    {
        '~' => 0,
        '&' => 1,
        '@' => 2,
        '%' => 3,
        '+' => 4,
        _ => 5
    };

    private static char RankPrefix(string user) =>
        string.IsNullOrWhiteSpace(user) ? '\0' : user.Trim()[0];

    private static string PlainNick(string user)
    {
        if (string.IsNullOrWhiteSpace(user)) return "";

        var clean = user.Trim();
        while (clean.Length > 0 && "~&@%+".Contains(clean[0]))
            clean = clean[1..];

        return clean;
    }

    private static bool IsPrivateMessage(string channel) =>
        channel.StartsWith("PM:", StringComparison.OrdinalIgnoreCase);

    private static string PlainChannelName(string channel) =>
        IsPrivateMessage(channel) ? channel[3..] : channel;

    private static class IrcMarkup
    {
        private sealed class Style
        {
            public bool Bold { get; set; }
            public bool Underline { get; set; }
            public bool Italic { get; set; }
            public bool Reverse { get; set; }
            public int? Foreground { get; set; }
            public int? Background { get; set; }

            public void Reset()
            {
                Bold = Underline = Italic = Reverse = false;
                Foreground = Background = null;
            }

            public string ClassName()
            {
                var classes = new List<string>();

                if (Bold) classes.Add("irc-bold");
                if (Underline) classes.Add("irc-underline");
                if (Italic) classes.Add("irc-italic");
                if (Reverse) classes.Add("irc-reverse");
                if (Foreground is { } fg) classes.Add($"irc-fg-{fg}");
                if (Background is { } bg) classes.Add($"irc-bg-{bg}");

                return string.Join(" ", classes);
            }
        }

        public static string ToHtml(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            var output = new StringBuilder(text.Length + 32);
            var segment = new StringBuilder();
            var style = new Style();

            void Flush()
            {
                if (segment.Length == 0) return;

                var encoded = WebUtility.HtmlEncode(segment.ToString());
                var classes = style.ClassName();

                output.Append(string.IsNullOrEmpty(classes)
                    ? encoded
                    : $"<span class=\"{classes}\">{encoded}</span>");

                segment.Clear();
            }

            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];

                switch (c)
                {
                    case '\x02':
                        Flush();
                        style.Bold = !style.Bold;
                        continue;

                    case '\x03':
                        Flush();
                        if (i + 1 >= text.Length || !char.IsDigit(text[i + 1]))
                        {
                            style.Foreground = null;
                            style.Background = null;
                            continue;
                        }

                        i++;
                        style.Foreground = ReadColor(text, ref i);

                        if (i + 2 < text.Length && text[i + 1] == ',' && char.IsDigit(text[i + 2]))
                        {
                            i += 2;
                            style.Background = ReadColor(text, ref i);
                        }
                        else
                        {
                            style.Background = null;
                        }

                        continue;

                    case '\x0F':
                        Flush();
                        style.Reset();
                        continue;

                    case '\x16':
                        Flush();
                        style.Reverse = !style.Reverse;
                        continue;

                    case '\x1D':
                        Flush();
                        style.Italic = !style.Italic;
                        continue;

                    case '\x1F':
                        Flush();
                        style.Underline = !style.Underline;
                        continue;
                }

                if (char.IsControl(c) && c != '\t')
                    continue;

                segment.Append(c);
            }

            Flush();
            return output.ToString();
        }

        private static int ReadColor(string text, ref int index)
        {
            var value = text[index] - '0';

            if (index + 1 < text.Length && char.IsDigit(text[index + 1]))
            {
                value = (value * 10) + text[index + 1] - '0';
                index++;
            }

            return value;
        }
    }
}
