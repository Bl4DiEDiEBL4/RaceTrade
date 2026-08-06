using RaceTrade.Engine.Logging;

namespace RaceTrade.Web.Services;

/// <summary>
/// Owns the engine's lifetime: starts an <see cref="IRCClient"/> per enabled site and
/// stops them again. This is the web equivalent of MainApp's Start/Stop trader button.
///
/// Kept deliberately close to the WinForms startup path (same config validation, same
/// per-site task-per-connection model) so behaviour does not drift between the two
/// builds while both exist.
/// </summary>
public sealed class EngineHost : IAsyncDisposable
{
    private readonly WebIrcOutput _output;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cts;
    private readonly List<Task> _siteTasks = new();
    private readonly Dictionary<string, IRCClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public EngineHost(WebIrcOutput output) => _output = output;

    public bool IsRunning { get; private set; }

    /// <summary>Sites that are connecting/connected, for the UI.</summary>
    public IReadOnlyCollection<string> ConnectedSites
    {
        get { lock (_clients) return _clients.Keys.ToList(); }
    }

    public async Task StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsRunning) return;

            _cts = new CancellationTokenSource();
            _siteTasks.Clear();
            lock (_clients) _clients.Clear();

            RaceHelper.LoadAllSiteConfigs();

            var started = 0;
            foreach (var siteName in EnumerateSiteNames())
            {
                if (!SiteConfigManager.TryGetSiteConfig(siteName, out var cfg) || cfg is null)
                    continue;

                if (cfg.SiteSettings?.DisableSite == true)
                    continue;

                // Same preconditions the WinForms build checked before dialling out -
                // a half-configured site should be skipped with a clear reason, not
                // throw somewhere inside the IRC client.
                if (string.IsNullOrEmpty(cfg.Server?.Host))
                { LogManager.Warning($"Skipping site '{siteName}': missing IRC host."); continue; }

                if (string.IsNullOrEmpty(cfg.Server?.Username))
                { LogManager.Warning($"Skipping site '{siteName}': missing IRC username."); continue; }

                if (string.IsNullOrEmpty(cfg.Server?.Password))
                { LogManager.Warning($"Skipping site '{siteName}': missing IRC password."); continue; }

                if (string.IsNullOrEmpty(cfg.SiteSettings?.BotName))
                { LogManager.Warning($"Skipping site '{siteName}': missing bot name."); continue; }

                var channels = new[] { cfg.SiteSettings.Chan1, cfg.SiteSettings.Chan2, cfg.SiteSettings.Chan3 }
                    .Where(c => !string.IsNullOrWhiteSpace(c))
                    .ToList();

                if (channels.Count == 0)
                { LogManager.Warning($"Skipping site '{siteName}': no IRC channels defined."); continue; }

                var token = _cts.Token;
                var name = siteName;
                var config = cfg;

                _siteTasks.Add(Task.Run(async () =>
                {
                    var client = new IRCClient(config, name, _output, token);
                    lock (_clients) _clients[name] = client;

                    try
                    {
                        LogManager.Info($"Connecting to ZNC for site '{name}'...");
                        await client.ConnectToZNCAsync();
                        LogManager.Success($"Site '{name}' disconnected cleanly.");
                    }
                    catch (OperationCanceledException)
                    {
                        // Normal on Stop.
                    }
                    catch (Exception ex)
                    {
                        LogManager.Error($"IRC error for site '{name}': {ex.Message}");
                    }
                    finally
                    {
                        lock (_clients) _clients.Remove(name);
                    }
                }, token));

                started++;
            }

            IsRunning = true;

            if (started == 0)
                LogManager.Warning("Racer started, but no site is ready to connect. Check the Sites page.");
            else
                LogManager.Success($"Racer started: connecting {started} site(s).");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsRunning) return;

            LogManager.Info("Stopping racer...");

            // Cancel first so the IRC read loops exit, then ask each client to close its
            // socket - cancellation alone leaves a blocking read parked on the socket.
            _cts?.Cancel();

            List<IRCClient> clients;
            lock (_clients) clients = _clients.Values.ToList();

            foreach (var c in clients)
            {
                try { c.Disconnect(); } catch { /* already gone */ }
            }

            // Bounded wait: a stuck socket must not hang the UI thread that pressed Stop.
            try
            {
                await Task.WhenAny(
                    Task.WhenAll(_siteTasks),
                    Task.Delay(TimeSpan.FromSeconds(5)));
            }
            catch { }

            _siteTasks.Clear();
            lock (_clients) _clients.Clear();

            _cts?.Dispose();
            _cts = null;
            IsRunning = false;

            LogManager.Success("Racer stopped.");
        }
        finally
        {
            _gate.Release();
        }
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
