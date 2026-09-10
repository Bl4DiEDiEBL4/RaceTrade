namespace RaceTrade.Web.Services;

public sealed class SiteConnectionTestService
{
    private readonly FxpBackendSiteService _fxpBackendSites;
    private readonly FxpClientService _fxp;
    private readonly SiteStore _siteStore;
    private readonly FxpBackendStore _fxpBackendStore;

    public SiteConnectionTestService(FxpBackendSiteService fxpBackendSites, FxpClientService fxp, SiteStore siteStore, FxpBackendStore fxpBackendStore)
    {
        _fxpBackendSites = fxpBackendSites;
        _fxp = fxp;
        _siteStore = siteStore;
        _fxpBackendStore = fxpBackendStore;
    }

    public async Task<SiteConnectionTestResult> ValidateAsync(string siteName)
    {
        var details = new List<string>();
        var resolved = await ResolveFxpBackendSiteAsync(siteName, details);
        if (resolved is null)
            return SiteConnectionTestResult.Fail($"FXP backend site '{siteName}' was not found.", details);

        var issues = ValidateFxpBackendSite(resolved.Model);
        details.AddRange(Describe(resolved));

        return issues.Count == 0
            ? new SiteConnectionTestResult(true, $"FXP backend site config OK for '{siteName}'.", details)
            : SiteConnectionTestResult.Fail($"FXP backend site config has issues for '{siteName}'.", issues.Concat(details).ToList());
    }

    public async Task<SiteConnectionTestResult> LoginAsync(string siteName)
    {
        var details = new List<string>();
        var resolved = await ResolveFxpBackendSiteAsync(siteName, details);
        if (resolved is null)
            return SiteConnectionTestResult.Fail($"FXP backend site '{siteName}' was not found.", details);

        details.AddRange(Describe(resolved));

        var addresses = SplitLines(resolved.Model.AddressesText);
        if (addresses.Count == 0)
            return SiteConnectionTestResult.Fail($"Cannot test FXP backend login for '{siteName}'.", details.Append("No FTP addresses configured in FXP backend.").ToList());

        var path = FxpClientService.NormalizePath(resolved.Model.BasePath);

        try
        {
            var started = DateTime.UtcNow;
            var items = await _fxp.BrowseAsync(resolved.Server, resolved.Model.Name, path);
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

            details.Add($"Listed path: {path}");
            details.Add($"Items returned: {items.Count}");
            details.Add($"Completed in {(int)elapsed} ms.");

            return new SiteConnectionTestResult(true, $"FXP backend login/list OK for '{siteName}'.", details);
        }
        catch (Exception ex)
        {
            details.Add($"Listed path: {path}");
            details.Add($"{ex.GetType().Name}: {ex.Message}");
            return SiteConnectionTestResult.Fail($"FXP backend login/list failed for '{siteName}'.", details);
        }
    }

    private async Task<ResolvedFxpBackendSite?> ResolveFxpBackendSiteAsync(string siteName, List<string> details)
    {
        SiteConfig? localConfig = null;
        try
        {
            localConfig = _siteStore.Load(siteName);
        }
        catch (Exception ex)
        {
            details.Add($"Could not load RaceTrade site config '{siteName}': {ex.Message}");
        }

        var fxpBackendSiteName = localConfig?.SiteSettings?.Sitename?.Trim();
        if (string.IsNullOrWhiteSpace(fxpBackendSiteName))
            fxpBackendSiteName = siteName;

        if (!string.Equals(fxpBackendSiteName, siteName, StringComparison.OrdinalIgnoreCase))
            details.Add($"RaceTrade config '{siteName}' maps to FXP backend site '{fxpBackendSiteName}'.");

        var allServers = _fxpBackendStore.Load().FxpBackends ?? new List<FxpBackend>();
        var servers = allServers
            .Where(s => FxpBackendStore.IsActive(s))
            .Where(s => !string.IsNullOrWhiteSpace(s.Host) && !string.IsNullOrWhiteSpace(s.Port))
            .ToList();

        if (servers.Count == 0)
        {
            details.Add("No FXP backends are configured.");
            return null;
        }

        var configuredServerId = localConfig?.SiteSettings?.FxpBackendId?.Trim();
        if (!string.IsNullOrWhiteSpace(configuredServerId))
        {
            var configured = allServers.FirstOrDefault(s =>
                string.Equals(s.Id, configuredServerId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(s.Name, configuredServerId, StringComparison.OrdinalIgnoreCase));

            if (configured is null)
            {
                details.Add($"Configured FXP backend '{configuredServerId}' is disabled or was not found.");
                return null;
            }

            if (configured.Disabled)
            {
                details.Add($"Configured FXP backend '{configuredServerId}' is currently disabled.");
                return null;
            }

            var server = configured;
            details.Add($"Using configured FXP backend '{DisplayValue(configuredServerId)}'.");
            return await TryResolveOnServerAsync(server, fxpBackendSiteName, details);
        }

        details.Add("No FXP backend mapping is stored for this RaceTrade site; checking all FXP backends.");
        foreach (var server in servers)
        {
            var resolved = await TryResolveOnServerAsync(server, fxpBackendSiteName, details);
            if (resolved is not null)
                return resolved;
        }

        return null;
    }

    private async Task<ResolvedFxpBackendSite?> TryResolveOnServerAsync(FxpBackend server, string siteName, List<string> details)
    {
        var label = DisplayServer(server);
        try
        {
            var names = await _fxpBackendSites.LoadSiteNamesAsync(server);
            var match = names.FirstOrDefault(n => string.Equals(n, siteName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                details.Add($"{label}: site '{siteName}' not found.");
                return null;
            }

            var model = await _fxpBackendSites.LoadSiteAsync(server, match);
            return new ResolvedFxpBackendSite(server, model);
        }
        catch (TaskCanceledException)
        {
            details.Add($"{label}: FXP backend API timed out.");
        }
        catch (Exception ex)
        {
            details.Add($"{label}: {ex.Message}");
        }

        return null;
    }

    private static List<string> ValidateFxpBackendSite(FxpBackendSiteEditModel model)
    {
        var issues = new List<string>();
        if (SplitLines(model.AddressesText).Count == 0)
            issues.Add("No FTP addresses configured in FXP backend.");

        if (string.IsNullOrWhiteSpace(model.User))
            issues.Add("FTP user is empty in FXP backend.");

        if (model.Disabled)
            issues.Add("FXP backend site is disabled.");

        return issues;
    }

    private static IEnumerable<string> Describe(ResolvedFxpBackendSite resolved)
    {
        var model = resolved.Model;
        var addresses = SplitLines(model.AddressesText);
        yield return $"FXP backend: {DisplayServer(resolved.Server)}";
        yield return $"FXP backend site: {model.Name}";
        yield return $"Endpoint: {FxpClientService.BuildEndpoint(resolved.Server)}";
        yield return $"Base path: {FxpClientService.NormalizePath(model.BasePath)}";
        yield return $"Addresses: {addresses.Count}";

        if (addresses.Count > 0)
            yield return $"First address: {addresses[0]}";

        yield return $"User: {DisplayValue(model.User)}";
        yield return $"Sections: {model.Sections.Count}";
        yield return $"Disabled: {(model.Disabled ? "yes" : "no")}";
    }

    private static string DisplayServer(FxpBackend server)
    {
        var name = string.IsNullOrWhiteSpace(server.Name) ? server.Id : server.Name;
        return $"{DisplayValue(name)} ({FxpClientService.BuildEndpoint(server)})";
    }

    private static List<string> SplitLines(string? value) =>
        (value ?? "")
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

    private static string DisplayValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "(empty)" : value.Trim();

    private sealed record ResolvedFxpBackendSite(FxpBackend Server, FxpBackendSiteEditModel Model);
}

public sealed record SiteConnectionTestResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Details)
{
    public static SiteConnectionTestResult Fail(string message, IReadOnlyList<string> details) =>
        new(false, message, details);
}
