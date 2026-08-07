using System.Collections.Concurrent;
using RaceTrade;
using RaceTrade.Engine.Compat;
using RaceTrade.Engine.Logging;

namespace RaceTrade.Web.Services;

/// <summary>
/// Chat IRC connections, completely independent of the trader.
///
/// This is the web equivalent of MainApp.StartIrcConnectionsOnly(): the WinForms build
/// opened its own set of <see cref="ChatIrcClient"/> instances in chat-only mode when the
/// tabbed IRC window was opened, with its own CancellationTokenSource, so you could chat
/// without the racer running (and stop the racer without losing chat).
///
/// Deliberately NOT folded into <see cref="EngineHost"/>: the two have different lifetimes
/// and different clients (IRCClient parses announces and fires races; ChatIrcClient only
/// tracks users and decrypts channel traffic). Sharing state between them is what would
/// couple chat to the trader again.
/// </summary>
public sealed class ChatHost : IAsyncDisposable
{
    private readonly WebIrcOutput _output;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cts;
    private readonly List<Task> _siteTasks = new();
    private readonly ConcurrentDictionary<string, ChatIrcClient> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    public ChatHost(WebIrcOutput output) => _output = output;

    public bool IsRunning { get; private set; }

    /// <summary>Raised when the running state changes, so the UI can re-render.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<string> ConnectedSites => _clients.Keys.ToList();

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _siteTasks.Clear();
            _clients.Clear();
            SiteConfigManager.Invalidate();

            var token = _cts.Token;
            var started = 0;

            foreach (var siteName in EnumerateSiteNames())
            {
                if (!SiteConfigManager.TryGetSiteConfig(siteName, out var cfg) || cfg is null)
                    continue;

                if (cfg.SiteSettings?.DisableSite == true)
                    continue;

                // Chat needs less than the racer does - no bot name check, no announce
                // channels - but the connection itself still needs these three.
                if (string.IsNullOrWhiteSpace(cfg.Server?.Host)) continue;
                if (string.IsNullOrWhiteSpace(cfg.Server?.Username)) continue;
                if (string.IsNullOrWhiteSpace(cfg.Server?.Password)) continue;

                var name = siteName;
                var config = cfg;

                _siteTasks.Add(Task.Run(async () =>
                {
                    ChatIrcClient client;
                    try
                    {
                        client = new ChatIrcClient(
                            config,
                            name,
                            (category, release) => { },   // announce callback unused in chat
                            _output,
                            token);
                    }
                    catch (Exception ex)
                    {
                        // The constructor throws on a half-filled config; report which
                        // site instead of failing the whole chat start.
                        LogManager.Warning($"Chat: skipping site '{name}': {ex.Message}");
                        return;
                    }

                    client.SetChatOnlyMode(true);        // joins Chan1..Chan20 + chat_keys
                    client.SetTabbedLogOutput(_output);
                    client.SetUserTrackingEnabled(true);

                    _clients[name] = client;
                    Changed?.Invoke();

                    // Create the tabs up front so the channel picker is populated while
                    // the connection is still being established.
                    foreach (var chan in ChannelsOf(config))
                    {
                        _output.EnsureChannel(name, chan);
                        _output.AppendChannelMessage(name, chan, $"*** Connecting to {chan}...",
                            Color.Gray);
                    }

                    try
                    {
                        await client.ConnectToZNCAsync();
                        LogManager.Success($"Chat disconnected from '{name}'.");
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal on Stop.
                    }
                    catch (Exception ex)
                    {
                        LogManager.Error($"Chat error for site '{name}': {ex.Message}");
                        foreach (var chan in ChannelsOf(config))
                            _output.AppendChannelMessage(name, chan, $"*** Connection failed: {ex.Message}",
                                Color.Red);
                    }
                    finally
                    {
                        _clients.TryRemove(name, out _);
                        Changed?.Invoke();
                    }
                }, token));

                started++;
            }

            if (started == 0)
            {
                _cts.Dispose();
                _cts = null;
                IsRunning = false;
                LogManager.Warning("Chat started, but no site has IRC credentials. Check the Sites page.");
            }
            else
            {
                IsRunning = true;
                LogManager.Success($"Chat started: connecting {started} site(s).");
            }
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning) return;

            LogManager.Info("Stopping chat...");

            // Cancel first so the read loops exit, then close the sockets - cancellation
            // alone leaves a blocking read parked on the socket.
            _cts?.Cancel();

            foreach (var c in _clients.Values)
            {
                try { c.Disconnect(); } catch { /* already gone */ }
            }

            try
            {
                await Task.WhenAny(Task.WhenAll(_siteTasks), Task.Delay(TimeSpan.FromSeconds(5)));
            }
            catch { }

            _siteTasks.Clear();
            _clients.Clear();

            _cts?.Dispose();
            _cts = null;
            IsRunning = false;

            LogManager.Success("Chat stopped.");
        }
        finally
        {
            _gate.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Sends a line to a channel on one site. No-op if that site is not connected.</summary>
    public async Task SendAsync(string siteName, string channel, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;

        if (!_clients.TryGetValue(siteName, out var client))
        {
            LogManager.Warning($"Chat: not connected to '{siteName}'.");
            return;
        }

        await client.SendChannelMessage(channel, message);

        // The client only echoes what it receives back from the server, and ZNC does not
        // echo our own PRIVMSG, so show it locally.
        _output.AppendChannelMessage(siteName, channel, $"<{NickOf(siteName)}> {message}",
            Color.White);
    }

    public async Task RequestUserListsAsync()
    {
        foreach (var client in _clients.Values)
        {
            try { await client.RequestUserList(); }
            catch { /* a single dead connection should not break the chat page */ }
        }
    }

    public string GetChannelKey(string siteName, string channel)
    {
        if (string.IsNullOrWhiteSpace(siteName) || string.IsNullOrWhiteSpace(channel))
            return "";

        return _clients.TryGetValue(siteName, out var client)
            ? client.GetChannelKey(channel)
            : "";
    }

    public void SetChannelKey(string siteName, string channel, string utf8Key, bool persist)
    {
        if (string.IsNullOrWhiteSpace(siteName) ||
            string.IsNullOrWhiteSpace(channel) ||
            string.IsNullOrWhiteSpace(utf8Key))
        {
            return;
        }

        if (!_clients.TryGetValue(siteName, out var client))
        {
            LogManager.Warning($"Chat: not connected to '{siteName}'.");
            return;
        }

        client.SetChannelKey(channel, utf8Key, persist);
        _output.AppendChannelMessage(
            siteName,
            channel,
            persist
                ? $"[FiSH] Blowfish key saved for {channel}"
                : $"[FiSH] Blowfish key set for {channel} until reconnect",
            Color.Green);
    }

    private string NickOf(string siteName) =>
        SiteConfigManager.TryGetSiteConfig(siteName, out var cfg) && cfg?.Server?.Username is { } u
            ? u.Split('/')[0]
            : "me";

    private static IEnumerable<string> ChannelsOf(SiteConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ss = config.SiteSettings;
        if (ss is null) yield break;

        for (int i = 1; i <= 20; i++)
        {
            var value = ss.GetType().GetProperty($"Chan{i}")?.GetValue(ss) as string;
            var channel = NormalizeChannel(value);
            if (!string.IsNullOrWhiteSpace(channel) && seen.Add(channel))
                yield return channel;
        }

        if (ss.ChatKeys is null) yield break;

        foreach (var key in ss.ChatKeys.Keys)
        {
            var channel = NormalizeChannel(key);
            if (channel.StartsWith("#") && seen.Add(channel))
                yield return channel;
        }
    }

    private static string NormalizeChannel(string? channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
            return "";

        channel = channel.Trim();
        if (!channel.StartsWith("#") && !channel.StartsWith("PM:", StringComparison.OrdinalIgnoreCase))
            channel = "#" + channel.TrimStart('#');

        return channel;
    }

    private static IEnumerable<string> EnumerateSiteNames()
    {
        if (!Directory.Exists("sites")) yield break;

        foreach (var file in Directory.GetFiles("sites", "*.json").OrderBy(f => f))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name is "new_site" or "template" or "example") continue;
            yield return name;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
