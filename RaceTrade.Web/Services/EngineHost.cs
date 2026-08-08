using RaceTrade.Engine.Logging;

using System.Text.RegularExpressions;

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
    private readonly PreBotStore _preBots;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private CancellationTokenSource? _cts;
    private readonly List<Task> _siteTasks = new();
    private readonly Dictionary<string, IRCClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    public EngineHost(WebIrcOutput output, PreBotStore preBots)
    {
        _output = output;
        _preBots = preBots;
    }

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

            SiteConfigManager.Invalidate();
            RaceHelper.LoadAllSiteConfigs();

            var started = 0;
            var readySites = CollectReadyClients(logSkips: false).ToList();
            if (readySites.Count == 0)
            {
                // A browser can hit Start very early after process launch. Re-read the
                // on-disk configs once before deciding there are truly no connectable
                // sites, so users do not need the old Stop/Start dance.
                await Task.Delay(250);
                SiteConfigManager.Invalidate();
                RaceHelper.LoadAllSiteConfigs();
                readySites = CollectReadyClients(logSkips: true).ToList();
            }

            foreach (var (siteName, cfg) in readySites)
            {
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

            if (started == 0)
            {
                _cts.Dispose();
                _cts = null;
                IsRunning = false;
                LogManager.Warning("Racer started, but no site is ready to connect. Check the Sites page.");
            }
            else
            {
                IsRunning = true;
                LogManager.Success($"Racer started: connecting {started} site(s).");
            }
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

    private IEnumerable<(string Name, SiteConfig Config)> CollectReadyClients(bool logSkips)
    {
        foreach (var site in CollectReadySites(logSkips))
            yield return site;

        foreach (var prebot in CollectGlobalPreBotClients(logSkips))
            yield return prebot;
    }

    private static IEnumerable<(string Name, SiteConfig Config)> CollectReadySites(bool logSkips)
    {
        foreach (var siteName in EnumerateSiteNames())
        {
            if (!SiteConfigManager.TryGetSiteConfig(siteName, out var cfg) || cfg is null)
            {
                if (logSkips) LogManager.Warning($"Skipping site '{siteName}': could not load site config.");
                continue;
            }

            if (cfg.SiteSettings?.DisableSite == true)
                continue;

            if (IsGlobalPreBotMode(cfg.SiteSettings?.PreOrSite))
                continue;

            // Same preconditions IRCClient enforces before dialling out. Keep this in
            // sync so the Start button does not reject a site the actual client accepts.
            if (string.IsNullOrWhiteSpace(cfg.Server?.Host))
            { if (logSkips) LogManager.Warning($"Skipping site '{siteName}': missing IRC host."); continue; }

            if (string.IsNullOrWhiteSpace(cfg.Server?.Username))
            { if (logSkips) LogManager.Warning($"Skipping site '{siteName}': missing IRC username."); continue; }

            if (RequiresPassword(cfg) && string.IsNullOrWhiteSpace(cfg.Server?.Password))
            { if (logSkips) LogManager.Warning($"Skipping site '{siteName}': missing IRC password."); continue; }

            if (string.IsNullOrWhiteSpace(cfg.SiteSettings?.BotName))
            { if (logSkips) LogManager.Warning($"Skipping site '{siteName}': missing bot name."); continue; }

            if (ConfiguredChannels(cfg.SiteSettings).Count == 0)
            { if (logSkips) LogManager.Warning($"Skipping site '{siteName}': no IRC channels defined."); continue; }

            yield return (siteName, cfg);
        }
    }

    private IEnumerable<(string Name, SiteConfig Config)> CollectGlobalPreBotClients(bool logSkips)
    {
        var sitesByPreBot = new Dictionary<string, List<SiteConfig>>(StringComparer.OrdinalIgnoreCase);

        foreach (var siteName in EnumerateSiteNames())
        {
            if (!SiteConfigManager.TryGetSiteConfig(siteName, out var cfg) || cfg is null)
                continue;

            if (cfg.SiteSettings?.DisableSite == true)
                continue;

            var mode = cfg.SiteSettings?.PreOrSite;
            if (!IsGlobalPreBotMode(mode))
                continue;

            var prebotName = ExtractGlobalPreBotName(mode);
            if (string.IsNullOrWhiteSpace(prebotName))
            {
                if (logSkips)
                    LogManager.Warning($"Site '{siteName}' uses Global PreBot, but no PreBot name is selected.");
                continue;
            }

            if (!sitesByPreBot.TryGetValue(prebotName, out var sites))
            {
                sites = new List<SiteConfig>();
                sitesByPreBot[prebotName] = sites;
            }

            sites.Add(cfg);
        }

        var availablePreBots = _preBots.ListNames();

        foreach (var pair in sitesByPreBot)
        {
            var prebotName = pair.Key;
            var linkedSites = pair.Value;

            if (!availablePreBots.Contains(prebotName, StringComparer.OrdinalIgnoreCase))
            {
                if (logSkips)
                    LogManager.Warning($"PreBot '{prebotName}' is selected by a site, but pre_bots\\{prebotName}.json was not found.");
                continue;
            }

            var prebotConfig = _preBots.Load(prebotName);
            if (!TryBuildGlobalPreBotConfig(prebotName, prebotConfig, linkedSites, logSkips, out var mergedConfig))
                continue;

            yield return (prebotName, mergedConfig);
        }
    }

    private static bool TryBuildGlobalPreBotConfig(
        string prebotName,
        PreBotConfig prebotConfig,
        List<SiteConfig> linkedSites,
        bool logSkips,
        out SiteConfig mergedConfig)
    {
        mergedConfig = null!;

        var firstSite = linkedSites.FirstOrDefault();
        if (firstSite is null)
            return false;

        var znc = prebotConfig.ZncServer ?? new ZncServerSettings();
        var settings = prebotConfig.SiteSettings ?? new PreBotSiteSettings();

        if (string.IsNullOrWhiteSpace(znc.Host))
        { if (logSkips) LogManager.Warning($"Skipping PreBot '{prebotName}': missing IRC host."); return false; }

        if (string.IsNullOrWhiteSpace(znc.Username))
        { if (logSkips) LogManager.Warning($"Skipping PreBot '{prebotName}': missing IRC username."); return false; }

        if (string.IsNullOrWhiteSpace(settings.BotName))
        { if (logSkips) LogManager.Warning($"Skipping PreBot '{prebotName}': missing bot name."); return false; }

        if (string.IsNullOrWhiteSpace(settings.Channel1))
        { if (logSkips) LogManager.Warning($"Skipping PreBot '{prebotName}': missing channel."); return false; }

        var enabledSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mergedSections = new List<Section>();

        foreach (var site in linkedSites)
        {
            foreach (var section in site.RaceSectionsEnabled ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(section))
                    enabledSections.Add(section);
            }

            foreach (var section in site.Sections ?? new List<Section>())
            {
                if (string.IsNullOrWhiteSpace(section.IrcName))
                    continue;

                if (!mergedSections.Any(s => string.Equals(s.IrcName, section.IrcName, StringComparison.OrdinalIgnoreCase)))
                    mergedSections.Add(section);
            }
        }

        mergedConfig = new SiteConfig
        {
            Server = new ServerSettings
            {
                Host = znc.Host,
                Port = znc.Port,
                Username = znc.Username,
                Password = znc.Password
            },
            SiteSettings = new SiteSettings
            {
                Sitename = firstSite.SiteSettings?.Sitename,
                BotName = settings.BotName,
                Chan1 = settings.Channel1,
                BlowfishKey1 = settings.BlowfishKey1,
                SectionRegexPattern = settings.SectionRegex,
                SectionPrefix = settings.SectionPrefix,
                SectionSuffix = settings.SectionSuffix,
                ReleaseRegexPattern = settings.NameRegex,
                PreOrSite = $"Global PreBot ({prebotName})"
            },
            RaceSectionsEnabled = enabledSections.ToList(),
            Sections = mergedSections,
            GlobalBlacklist = firstSite.GlobalBlacklist ?? new List<string>(),
            Affils = firstSite.Affils ?? new List<string>()
        };

        LogManager.Success($"PreBot '{prebotName}' configured for {linkedSites.Count} site(s), monitoring {enabledSections.Count} section(s).");
        return true;
    }

    private static bool RequiresPassword(SiteConfig cfg)
    {
        var mode = cfg.SiteSettings?.PreOrSite;
        var isGlobalPrebot = mode?.StartsWith("Global PreBot", StringComparison.OrdinalIgnoreCase) == true;
        var isPrebot = string.Equals(mode, "PreBot", StringComparison.OrdinalIgnoreCase);
        return !isGlobalPrebot && !isPrebot;
    }

    private static bool IsGlobalPreBotMode(string? mode) =>
        mode?.StartsWith("Global PreBot", StringComparison.OrdinalIgnoreCase) == true;

    private static string? ExtractGlobalPreBotName(string? mode)
    {
        if (!IsGlobalPreBotMode(mode))
            return null;

        var match = Regex.Match(mode ?? "", @"\((.*?)\)");
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static List<string> ConfiguredChannels(SiteSettings? settings)
    {
        var channels = new List<string>();
        if (settings is null)
            return channels;

        for (var i = 1; i <= 20; i++)
        {
            var value = settings.GetType().GetProperty($"Chan{i}")?.GetValue(settings) as string;
            if (!string.IsNullOrWhiteSpace(value))
                channels.Add(value);
        }

        return channels;
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
