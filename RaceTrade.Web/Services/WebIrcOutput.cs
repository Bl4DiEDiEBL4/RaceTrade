using System.Collections.Concurrent;
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

    public sealed record ChatLine(DateTimeOffset At, string Text, LogLevel Level);

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
        q.Enqueue(new ChatLine(DateTimeOffset.Now, message, color.Level));

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
        _lines.Keys.OrderBy(k => k.Site).ThenBy(k => k.Channel).ToList();

    public IReadOnlyList<ChatLine> Lines(string site, string channel) =>
        _lines.TryGetValue(Key(site, channel), out var q) ? q.ToArray() : Array.Empty<ChatLine>();

    public IReadOnlyList<string> Users(string site, string channel) =>
        _users.TryGetValue(Key(site, channel), out var set)
            ? set.Keys.OrderBy(u => u, StringComparer.OrdinalIgnoreCase).ToList()
            : Array.Empty<string>();

    private static (string, string) Key(string site, string channel) =>
        (site ?? "", channel ?? "");
}
