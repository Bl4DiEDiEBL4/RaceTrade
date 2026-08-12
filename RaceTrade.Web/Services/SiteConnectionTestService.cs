namespace RaceTrade.Web.Services;

public sealed class SiteConnectionTestService
{
    private readonly CbftpSiteService _cbftpSites;
    private readonly FxpClientService _fxp;

    public SiteConnectionTestService(CbftpSiteService cbftpSites, FxpClientService fxp)
    {
        _cbftpSites = cbftpSites;
        _fxp = fxp;
    }

    public async Task<SiteConnectionTestResult> ValidateAsync(string siteName)
    {
        var details = new List<string>();
        var resolved = await ResolveCbftpSiteAsync(siteName, details);
        if (resolved is null)
            return SiteConnectionTestResult.Fail($"CBFTP site '{siteName}' was not found.", details);

        var issues = ValidateCbftpSite(resolved.Model);
        details.AddRange(Describe(resolved));

        return issues.Count == 0
            ? new SiteConnectionTestResult(true, $"CBFTP site config OK for '{siteName}'.", details)
            : SiteConnectionTestResult.Fail($"CBFTP site config has issues for '{siteName}'.", issues.Concat(details).ToList());
    }

    public async Task<SiteConnectionTestResult> LoginAsync(string siteName)
    {
        var details = new List<string>();
        var resolved = await ResolveCbftpSiteAsync(siteName, details);
        if (resolved is null)
            return SiteConnectionTestResult.Fail($"CBFTP site '{siteName}' was not found.", details);

        details.AddRange(Describe(resolved));

        var addresses = SplitLines(resolved.Model.AddressesText);
        if (addresses.Count == 0)
            return SiteConnectionTestResult.Fail($"Cannot test CBFTP login for '{siteName}'.", details.Append("No FTP addresses configured in CBFTP.").ToList());

        var path = FxpClientService.NormalizePath(resolved.Model.BasePath);

        try
        {
            var started = DateTime.UtcNow;
            var items = await _fxp.BrowseAsync(resolved.Server, resolved.Model.Name, path);
            var elapsed = (DateTime.UtcNow - started).TotalMilliseconds;

            details.Add($"Listed path: {path}");
            details.Add($"Items returned: {items.Count}");
            details.Add($"Completed in {(int)elapsed} ms.");

            return new SiteConnectionTestResult(true, $"CBFTP login/list OK for '{siteName}'.", details);
        }
        catch (Exception ex)
        {
            details.Add($"Listed path: {path}");
            details.Add($"{ex.GetType().Name}: {ex.Message}");
            return SiteConnectionTestResult.Fail($"CBFTP login/list failed for '{siteName}'.", details);
        }
    }

    private async Task<ResolvedCbftpSite?> ResolveCbftpSiteAsync(string siteName, List<string> details)
    {
        var servers = _cbftpSites.LoadServers()
            .Where(s => !string.IsNullOrWhiteSpace(s.Host) && !string.IsNullOrWhiteSpace(s.Port))
            .ToList();

        if (servers.Count == 0)
        {
            details.Add("No CBFTP servers are configured.");
            return null;
        }

        foreach (var server in servers)
        {
            var label = DisplayServer(server);
            try
            {
                var names = await _cbftpSites.LoadSiteNamesAsync(server);
                var match = names.FirstOrDefault(n => string.Equals(n, siteName, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                {
                    details.Add($"{label}: site '{siteName}' not found.");
                    continue;
                }

                var model = await _cbftpSites.LoadSiteAsync(server, match);
                return new ResolvedCbftpSite(server, model);
            }
            catch (TaskCanceledException)
            {
                details.Add($"{label}: CBFTP API timed out.");
            }
            catch (Exception ex)
            {
                details.Add($"{label}: {ex.Message}");
            }
        }

        return null;
    }

    private static List<string> ValidateCbftpSite(CbftpSiteEditModel model)
    {
        var issues = new List<string>();
        if (SplitLines(model.AddressesText).Count == 0)
            issues.Add("No FTP addresses configured in CBFTP.");

        if (string.IsNullOrWhiteSpace(model.User))
            issues.Add("FTP user is empty in CBFTP.");

        if (model.Disabled)
            issues.Add("CBFTP site is disabled.");

        return issues;
    }

    private static IEnumerable<string> Describe(ResolvedCbftpSite resolved)
    {
        var model = resolved.Model;
        var addresses = SplitLines(model.AddressesText);
        yield return $"CBFTP server: {DisplayServer(resolved.Server)}";
        yield return $"CBFTP site: {model.Name}";
        yield return $"Endpoint: {FxpClientService.BuildEndpoint(resolved.Server)}";
        yield return $"Base path: {FxpClientService.NormalizePath(model.BasePath)}";
        yield return $"Addresses: {addresses.Count}";

        if (addresses.Count > 0)
            yield return $"First address: {addresses[0]}";

        yield return $"User: {DisplayValue(model.User)}";
        yield return $"Sections: {model.Sections.Count}";
        yield return $"Disabled: {(model.Disabled ? "yes" : "no")}";
    }

    private static string DisplayServer(CbftpServer server)
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

    private sealed record ResolvedCbftpSite(CbftpServer Server, CbftpSiteEditModel Model);
}

public sealed record SiteConnectionTestResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Details)
{
    public static SiteConnectionTestResult Fail(string message, IReadOnlyList<string> details) =>
        new(false, message, details);
}
