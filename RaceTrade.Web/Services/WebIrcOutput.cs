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
    private int _notifyScheduled;

    // (site, channel) -> recent lines, and -> nick => status prefix ('\0' = plain user).
    //
    // Both keyed case-insensitively: IRC channel names and nicks are case-insensitive,
    // and the name we get back from the server ("#Site-Chat") often differs in case from
    // the one in the site config ("#site-chat"). With an ordinal key that produced two
    // separate entries — a chat tab with messages and a *different* one holding the user
    // list, which is why the list rendered empty.
    private readonly ConcurrentDictionary<(string Site, string Channel), ConcurrentQueue<ChatLine>> _lines =
        new(ChannelKeyComparer.Instance);

    private readonly ConcurrentDictionary<(string Site, string Channel), ConcurrentDictionary<string, char>> _users =
        new(ChannelKeyComparer.Instance);

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
        _users.GetOrAdd(key, _ => new ConcurrentDictionary<string, char>(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Drops a channel's buffered lines and member list, which removes its tab.
    ///
    /// A view-level action: the IRC client stays joined. If traffic arrives afterwards
    /// the tab simply comes back, which is what you want for a channel and harmless for
    /// a PM.
    /// </summary>
    public void CloseChannel(string siteName, string channelName)
    {
        var key = Key(siteName, channelName);
        _lines.TryRemove(key, out _);
        _users.TryRemove(key, out _);
        ScheduleChanged();
    }

    /// <summary>Clears all visible chat tabs and user lists after chat disconnects.</summary>
    public void Clear()
    {
        _lines.Clear();
        _users.Clear();
        ScheduleChanged();
    }

    public void AppendChannelMessage(string siteName, string channelName, string message, Color color)
    {
        var q = _lines.GetOrAdd(Key(siteName, channelName), _ => new ConcurrentQueue<ChatLine>());
        q.Enqueue(new ChatLine(DateTimeOffset.Now, message, IrcMarkup.ToHtml(message), color.Level));

        // Bound the buffer without locking; a brief overshoot is fine.
        while (q.Count > MaxLinesPerChannel && q.TryDequeue(out _)) { }

        ScheduleChanged();
    }

    // Nick and status prefix are stored SEPARATELY, not as the raw "@nick" string the
    // engine hands us. NAMES delivers prefixed nicks while JOIN/PART/NICK deliver bare
    // ones; keeping the raw form meant a PART for "bob" could never remove the "@bob"
    // that NAMES had inserted, and the list slowly filled with ghosts.

    public void AddUser(string siteName, string channelName, string username)
    {
        var (nick, prefix) = SplitPrefix(username);
        if (nick.Length == 0) return;

        _users.GetOrAdd(Key(siteName, channelName),
            _ => new ConcurrentDictionary<string, char>(StringComparer.OrdinalIgnoreCase))[nick] = prefix;

        ScheduleChanged();
    }

    public void RemoveUser(string siteName, string channelName, string username)
    {
        var (nick, _) = SplitPrefix(username);

        if (_users.TryGetValue(Key(siteName, channelName), out var set))
            set.TryRemove(nick, out _);

        ScheduleChanged();
    }

    public void UpdateUserList(string siteName, string channelName, List<string> users)
    {
        var set = new ConcurrentDictionary<string, char>(StringComparer.OrdinalIgnoreCase);

        foreach (var u in users ?? new List<string>())
        {
            var (nick, prefix) = SplitPrefix(u);
            if (nick.Length > 0) set[nick] = prefix;
        }

        _users[Key(siteName, channelName)] = set;
        ScheduleChanged();
    }

    // ---- read side, for the Blazor chat page --------------------------------------

    public event Action? Changed;

    private void ScheduleChanged()
    {
        if (Interlocked.Exchange(ref _notifyScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            Interlocked.Exchange(ref _notifyScheduled, 0);
            Changed?.Invoke();
        });
    }

    public IReadOnlyList<(string Site, string Channel)> Channels =>
        _lines.Keys
            .OrderBy(k => k.Site)
            .ThenBy(k => IsPrivateMessage(k.Channel))
            .ThenBy(k => PlainChannelName(k.Channel), StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<ChatLine> Lines(string site, string channel) =>
        _lines.TryGetValue(Key(site, channel), out var q) ? q.ToArray() : Array.Empty<ChatLine>();

    /// <summary>
    /// Members of a channel as "@nick" style strings, ordered owner → admin → op →
    /// half-op → voice → plain user, then alphabetically within each rank.
    /// </summary>
    public IReadOnlyList<string> Users(string site, string channel) =>
        _users.TryGetValue(Key(site, channel), out var set)
            ? set.ToArray()                       // snapshot: the IRC thread keeps writing
                .OrderBy(kv => Rank(kv.Value))
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kv => kv.Value == '\0' ? kv.Key : kv.Value + kv.Key)
                .ToList()
            : Array.Empty<string>();

    private static (string, string) Key(string site, string channel) =>
        (site ?? "", channel ?? "");

    private const string StatusPrefixes = "~&@%+";

    private static int Rank(char prefix) => prefix switch
    {
        '~' => 0,   // owner
        '&' => 1,   // admin
        '@' => 2,   // op
        '%' => 3,   // half-op
        '+' => 4,   // voice
        _ => 5      // regular
    };

    /// <summary>Splits "@nick" into ("nick", '@'). Keeps only the highest prefix.</summary>
    private static (string Nick, char Prefix) SplitPrefix(string user)
    {
        if (string.IsNullOrWhiteSpace(user)) return ("", '\0');

        var s = user.Trim();
        var prefix = '\0';

        // Some servers list every mode a user holds ("@+bob"); the first one is the
        // highest, which is the one worth showing.
        while (s.Length > 0 && StatusPrefixes.IndexOf(s[0]) >= 0)
        {
            if (prefix == '\0') prefix = s[0];
            s = s[1..];
        }

        return (s, prefix);
    }

    /// <summary>
    /// Case-insensitive key comparer for (site, channel). Both are case-insensitive in
    /// IRC, and the server's spelling routinely differs from the config's.
    /// </summary>
    private sealed class ChannelKeyComparer : IEqualityComparer<(string Site, string Channel)>
    {
        public static readonly ChannelKeyComparer Instance = new();

        public bool Equals((string Site, string Channel) a, (string Site, string Channel) b) =>
            string.Equals(a.Site, b.Site, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Channel, b.Channel, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Site, string Channel) k) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Site ?? ""),
            StringComparer.OrdinalIgnoreCase.GetHashCode(k.Channel ?? ""));
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
