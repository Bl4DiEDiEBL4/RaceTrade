using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RaceTrade.Web.Services;

public sealed class PreSpreadService
{
    private const string ConfigFolder = "pre";
    private const string ServersFile = "pre/cbftp_servers.json";
    private const string SitesFile = "pre/sites.json";

    private readonly CbftpStore _cbftpStore;
    private readonly FxpClientService _fxp;

    public PreSpreadService(CbftpStore cbftpStore, FxpClientService fxp)
    {
        _cbftpStore = cbftpStore;
        _fxp = fxp;
    }

    public List<PreCbftpServer> LoadServers()
    {
        EnsureDirectory();
        if (!File.Exists(ServersFile))
            return ImportServersFromMainConfig(save: false);

        var config = JsonConvert.DeserializeObject<PreCbftpServersConfig>(File.ReadAllText(ServersFile));
        return config?.Servers ?? new List<PreCbftpServer>();
    }

    public void SaveServers(List<PreCbftpServer> servers)
    {
        EnsureDirectory();
        foreach (var server in servers)
            server.Password = SecureConfig.EncryptIfNeeded(server.Password);

        AtomicFile.WriteAllText(
            ServersFile,
            JsonConvert.SerializeObject(new PreCbftpServersConfig { Servers = servers }, Formatting.Indented));
    }

    /// <summary>
    /// A server's identity, stable across imports.
    ///
    /// This used to fall back to <c>Guid.NewGuid()</c> when the main cbftp config had no
    /// id, so every "Import CBFTP servers" minted brand new ids. Sites were deduped on
    /// (name, server id), so after a re-import nothing matched and every site was added a
    /// second time — and the sites saved earlier pointed at ids that no longer existed,
    /// which is why "Source CBFTP server not found" appeared and releases never loaded.
    ///
    /// Host:port is the real identity of a cbftp instance, so it is what we key on.
    /// </summary>
    private static string StableId(CbftpServer s) =>
        !string.IsNullOrWhiteSpace(s.Id) ? s.Id!
        : !string.IsNullOrWhiteSpace(s.Host) ? $"{s.Host}:{s.Port}"
        : s.Name ?? "cbftp";

    /// <summary>
    /// Pulls the cbftp servers from the main config and MERGES them into the Pre server
    /// list: existing entries keep their id (so the sites pointing at them keep working)
    /// and only get their address/password refreshed.
    /// </summary>
    public List<PreCbftpServer> ImportServersFromMainConfig(bool save = true)
    {
        var existing = File.Exists(ServersFile)
            ? JsonConvert.DeserializeObject<PreCbftpServersConfig>(File.ReadAllText(ServersFile))?.Servers
              ?? new List<PreCbftpServer>()
            : new List<PreCbftpServer>();

        foreach (var s in _cbftpStore.Load().CbftpServers)
        {
            var id = StableId(s);

            // Match on id first, then on address: a server that was imported before the
            // ids were stable still has to be recognised instead of duplicated.
            var match = existing.FirstOrDefault(e =>
                            string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))
                        ?? existing.FirstOrDefault(e =>
                            !string.IsNullOrWhiteSpace(s.Host) &&
                            string.Equals(e.Host, s.Host, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(e.Port, s.Port, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                existing.Add(new PreCbftpServer
                {
                    Id = id,
                    Name = s.Name ?? s.Id ?? "cbftp",
                    Host = s.Host ?? "",
                    Port = s.Port ?? "",
                    Password = s.Password ?? "",
                    Profile = s.Profile ?? ""
                });
                continue;
            }

            match.Name = s.Name ?? match.Name;
            match.Host = s.Host ?? match.Host;
            match.Port = s.Port ?? match.Port;
            match.Profile = s.Profile ?? match.Profile;

            if (!string.IsNullOrWhiteSpace(s.Password))
                match.Password = s.Password;
        }

        if (save)
            SaveServers(existing);

        return existing;
    }

    /// <summary>
    /// Removes a cbftp server from the Pre config, together with the Pre sites that were
    /// reachable only through it — leaving them behind would just produce rows that can
    /// never load a listing.
    /// </summary>
    public PreActionResult RemoveServer(
        string serverId,
        List<PreCbftpServer> servers,
        List<PreSiteConfig> sites)
    {
        var server = servers.FirstOrDefault(s =>
            string.Equals(s.Id, serverId, StringComparison.OrdinalIgnoreCase));

        if (server is null)
            return PreActionResult.Fail("Server not found.");

        var orphaned = sites
            .Where(s => string.Equals(s.CbftpServerId, serverId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        servers.Remove(server);
        foreach (var site in orphaned)
            sites.Remove(site);

        SaveServers(servers);
        SaveSites(sites);

        var logs = orphaned.Select(s => $"removed site {s.Name}").ToList();

        return new PreActionResult(
            true,
            orphaned.Count == 0
                ? $"Removed server {server.Name}."
                : $"Removed server {server.Name} and {orphaned.Count} site(s) that used it.",
            logs);
    }

    /// <summary>
    /// Loads the Pre sites, dropping duplicates and repairing entries that point at a
    /// cbftp server which no longer exists (the legacy of the old random ids).
    /// </summary>
    public List<PreSiteConfig> LoadSites(IReadOnlyList<PreCbftpServer>? servers = null)
    {
        EnsureDirectory();
        if (!File.Exists(SitesFile))
            return new List<PreSiteConfig>();

        var config = JsonConvert.DeserializeObject<PreSitesConfig>(File.ReadAllText(SitesFile));
        var sites = config?.Sites ?? new List<PreSiteConfig>();

        var repaired = Deduplicate(sites);
        var rebound = false;

        if (servers is { Count: > 0 })
        {
            foreach (var site in repaired)
            {
                var known = servers.Any(s =>
                    string.Equals(s.Id, site.CbftpServerId, StringComparison.OrdinalIgnoreCase));

                // Dangling reference. With a single server there is only one sensible
                // answer, so bind it rather than leaving the site permanently broken.
                if (!known && servers.Count == 1)
                {
                    site.CbftpServerId = servers[0].Id;
                    rebound = true;
                }
            }

            repaired = Deduplicate(repaired);
        }

        // Rebinding does not change the count, so it has to be tracked separately —
        // otherwise the repair is silently redone on every single load.
        if (rebound || repaired.Count != sites.Count)
            SaveSites(repaired);

        return repaired;
    }

    /// <summary>
    /// One entry per (site name, cbftp server). Keeps the first, which is the one the
    /// user has been editing — a later duplicate only ever carries defaults.
    /// </summary>
    private static List<PreSiteConfig> Deduplicate(IEnumerable<PreSiteConfig> sites)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PreSiteConfig>();

        foreach (var site in sites)
        {
            if (string.IsNullOrWhiteSpace(site.Name))
                continue;

            if (seen.Add($"{site.Name}\u0000{site.CbftpServerId}"))
                result.Add(site);
        }

        return result;
    }

    public void SaveSites(List<PreSiteConfig> sites)
    {
        EnsureDirectory();
        AtomicFile.WriteAllText(
            SitesFile,
            JsonConvert.SerializeObject(new PreSitesConfig { Sites = sites }, Formatting.Indented));
    }

    public async Task<PreActionResult> FetchAllSitesAsync(List<PreCbftpServer> servers, List<PreSiteConfig> existingSites)
    {
        var logs = new List<string>();
        var imported = 0;

        foreach (var server in servers)
        {
            try
            {
                var password = DecryptPassword(server);
                var result = await CbftpSync.FetchSitesFromCbftp(server.Host ?? "", server.Port ?? "", password);
                if (!result.IsSuccess)
                {
                    logs.Add($"{server.Name}: {result.ErrorMessage}");
                    continue;
                }

                var skipped = 0;

                foreach (var site in result.Sites.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
                {
                    // Matched on name alone when the server is the same instance, so a
                    // site the user already configured (custom affil dir, section) is
                    // never replaced by a default copy.
                    if (existingSites.Any(s =>
                            string.Equals(s.Name, site.Name, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(s.CbftpServerId, server.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        skipped++;
                        continue;
                    }

                    existingSites.Add(new PreSiteConfig
                    {
                        Name = site.Name,
                        CbftpServerId = server.Id,
                        AffilDirectory = "/pre",
                        Section = "DEFAULT",
                        Enabled = true
                    });
                    imported++;
                }

                logs.Add(skipped > 0
                    ? $"{server.Name}: {result.Sites.Count} site(s), {skipped} already configured"
                    : $"{server.Name}: fetched {result.Sites.Count} site(s)");
            }
            catch (Exception ex)
            {
                logs.Add($"{server.Name}: {ex.Message}");
            }
        }

        var cleaned = Deduplicate(existingSites);
        var removed = existingSites.Count - cleaned.Count;
        if (removed > 0)
            logs.Add($"Removed {removed} duplicate site entr{(removed == 1 ? "y" : "ies")}.");

        existingSites.Clear();
        existingSites.AddRange(cleaned);

        SaveSites(existingSites);

        var message = imported == 0
            ? (removed > 0 ? $"No new Pre sites; cleaned up {removed} duplicate(s)." : "No new Pre sites found.")
            : $"Added {imported} Pre site(s).";

        return new PreActionResult(imported > 0 || removed > 0, message, logs);
    }

    public async Task<IReadOnlyList<string>> ListReleasesAsync(
        PreCbftpServer server,
        PreSiteConfig sourceSite)
    {
        var path = FxpClientService.NormalizePath(sourceSite.AffilDirectory ?? "/pre");

        try
        {
            var items = await _fxp.BrowseAsync(ToCbftpServer(server), sourceSite.Name ?? "", path);

            return items
                .Where(i => i.IsDirectory && i.Name is not "." and not "..")
                .Select(i => i.Name)
                .OrderByDescending(s => s, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            // Say WHERE it failed. "Object reference not set" tells you nothing when the
            // real problem is a wrong affil directory or a cbftp that is not reachable.
            throw new InvalidOperationException(
                $"Could not list {sourceSite.Name}:{path} via {server.Name} ({server.Host}:{server.Port}) - {ex.Message}", ex);
        }
    }

    /// <summary>Quick reachability check for one Pre site, for the Test button.</summary>
    public async Task<PreActionResult> TestSiteAsync(PreSiteConfig site, IReadOnlyList<PreCbftpServer> servers)
    {
        var server = FindServer(servers, site.CbftpServerId);
        if (server is null)
        {
            return PreActionResult.Fail(
                $"{site.Name}: no cbftp server bound. Pick one under CBFTP server, then Save config.");
        }

        var path = FxpClientService.NormalizePath(site.AffilDirectory ?? "/pre");

        try
        {
            var items = await _fxp.BrowseAsync(ToCbftpServer(server), site.Name ?? "", path);
            var dirs = items.Count(i => i.IsDirectory);

            return new PreActionResult(true,
                $"{site.Name}: OK - {dirs} director{(dirs == 1 ? "y" : "ies")} in {path}",
                new[] { $"via {server.Name} ({server.Host}:{server.Port})" });
        }
        catch (Exception ex)
        {
            return PreActionResult.Fail($"{site.Name}: {ex.Message}");
        }
    }

    public async Task<PreActionResult> DistributeAsync(
        string release,
        PreSiteConfig source,
        IReadOnlyList<PreSiteConfig> destinations,
        IReadOnlyList<PreCbftpServer> servers)
    {
        var logs = new List<string>();
        var sourceServer = FindServer(servers, source.CbftpServerId);
        if (sourceServer is null)
            return PreActionResult.Fail("Source CBFTP server not found.");

        var success = 0;
        var failed = 0;
        var password = DecryptPassword(sourceServer);
        var sourcePath = FxpClientService.NormalizePath(source.AffilDirectory);

        foreach (var destination in destinations.Where(d => !string.Equals(d.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var destinationPath = FxpClientService.NormalizePath(destination.AffilDirectory);
                var result = await CbftpRacer.StartTransferJobFxp(
                    srcSite: source.Name ?? "",
                    srcSectionOrPath: sourcePath,
                    srcIsSection: false,
                    dstSite: destination.Name ?? "",
                    dstPath: destinationPath,
                    releaseName: release,
                    host: sourceServer.Host ?? "",
                    port: sourceServer.Port ?? "",
                    password: password,
                    serverName: sourceServer.Name ?? sourceServer.Id ?? "cbftp");

                if (result.Success)
                {
                    success++;
                    logs.Add($"FXP queued: {source.Name} -> {destination.Name} ({release})");
                }
                else
                {
                    failed++;
                    logs.Add($"FXP failed: {destination.Name}: {result.ErrorMessage}");
                }
            }
            catch (Exception ex)
            {
                failed++;
                logs.Add($"FXP failed: {destination.Name}: {ex.Message}");
            }
        }

        return new PreActionResult(failed == 0, $"Distribution queued: {success}, failed: {failed}", logs);
    }

    public async Task<PreActionResult> SendPreAsync(
        string release,
        IReadOnlyList<PreSiteConfig> sites,
        IReadOnlyList<PreCbftpServer> servers)
    {
        var logs = new List<string>();
        var tasks = sites
            .Where(s => s.Enabled)
            .Select(site => SendPreToSiteAsync(release, site, servers, logs))
            .ToList();

        await Task.WhenAll(tasks);
        var success = logs.Count(l => l.StartsWith("OK ", StringComparison.Ordinal));
        var failed = logs.Count - success;
        return new PreActionResult(failed == 0, $"SITE PRE sent: {success}, failed: {failed}", logs);
    }

    public async Task<PreActionResult> DeleteReleaseAsync(
        string release,
        IReadOnlyList<PreSiteConfig> sites,
        IReadOnlyList<PreCbftpServer> servers)
    {
        var logs = new List<string>();
        var success = 0;
        var failed = 0;

        foreach (var site in sites)
        {
            var server = FindServer(servers, site.CbftpServerId);
            if (server is null)
            {
                failed++;
                logs.Add($"{site.Name}: CBFTP server not found");
                continue;
            }

            var path = FxpClientService.CombinePath(site.AffilDirectory ?? "/pre", release);
            var result = await DeletePathAsync(server, site.Name ?? "", path);
            if (result)
            {
                success++;
                logs.Add($"Deleted {site.Name}:{path}");
            }
            else
            {
                failed++;
                logs.Add($"Delete failed {site.Name}:{path}");
            }
        }

        return new PreActionResult(failed == 0, $"Deleted: {success}, failed: {failed}", logs);
    }

    public async Task<PreActionResult> CheckCompletionAsync(
        string release,
        PreSiteConfig source,
        IReadOnlyList<PreSiteConfig> sites,
        IReadOnlyList<PreCbftpServer> servers)
    {
        var logs = new List<string>();
        var sourceServer = FindServer(servers, source.CbftpServerId);
        if (sourceServer is null)
            return PreActionResult.Fail("Source CBFTP server not found.");

        var sourcePath = FxpClientService.CombinePath(source.AffilDirectory ?? "/pre", release);
        var sourceFiles = await GetFileListRecursiveAsync(sourceServer, source.Name ?? "", sourcePath);
        if (sourceFiles.Count == 0)
            return PreActionResult.Fail($"No files found on source {source.Name}:{sourcePath}.");

        var complete = 0;
        var incomplete = 0;

        foreach (var site in sites.Where(s => !string.Equals(s.Name, source.Name, StringComparison.OrdinalIgnoreCase)))
        {
            var server = FindServer(servers, site.CbftpServerId);
            if (server is null)
            {
                incomplete++;
                logs.Add($"{site.Name}: CBFTP server not found");
                continue;
            }

            var destinationPath = FxpClientService.CombinePath(site.AffilDirectory ?? "/pre", release);
            var destinationFiles = await GetFileListRecursiveAsync(server, site.Name ?? "", destinationPath);
            var problems = CompareFiles(sourceFiles, destinationFiles);

            if (problems.Count == 0)
            {
                complete++;
                logs.Add($"Complete: {site.Name} ({sourceFiles.Count} files)");
            }
            else
            {
                incomplete++;
                logs.Add($"Incomplete: {site.Name} - {problems.Count} problem(s)");

                // Naming them is the whole point: "incomplete" alone leaves you opening
                // an FTP client to find out what is actually missing.
                foreach (var problem in problems.Take(5))
                    logs.Add($"    {problem}");

                if (problems.Count > 5)
                    logs.Add($"    ... and {problems.Count - 5} more");
            }
        }

        return new PreActionResult(incomplete == 0, $"Completion: {complete} complete, {incomplete} incomplete", logs);
    }

    private async Task SendPreToSiteAsync(
        string release,
        PreSiteConfig site,
        IReadOnlyList<PreCbftpServer> servers,
        List<string> logs)
    {
        var server = FindServer(servers, site.CbftpServerId);
        if (server is null)
        {
            lock (logs) logs.Add($"{site.Name}: CBFTP server not found");
            return;
        }

        var command = $"SITE PRE {release} {site.Section}";
        try
        {
            var response = await SendRawAsync(server, site.Name ?? "", command);
            lock (logs)
            {
                logs.Add(response ? $"OK {site.Name}: {command}" : $"FAIL {site.Name}: {command}");
            }
        }
        catch (Exception ex)
        {
            lock (logs) logs.Add($"FAIL {site.Name}: {ex.Message}");
        }
    }

    private async Task<bool> SendRawAsync(PreCbftpServer server, string siteName, string command)
    {
        using var client = CreateClient(server, 30);
        var endpoint = BuildEndpoint(server);
        var payload = new { command, sites = new[] { siteName } };
        var json = JsonConvert.SerializeObject(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{endpoint}/raw", content);
        return response.IsSuccessStatusCode;
    }

    private async Task<bool> DeletePathAsync(PreCbftpServer server, string siteName, string path)
    {
        using var client = CreateClient(server, 60);
        var endpoint = BuildEndpoint(server);
        var url = $"{endpoint}/path?site={Uri.EscapeDataString(siteName)}&path={Uri.EscapeDataString(path)}&type=OWN";
        var response = await client.DeleteAsync(url);
        return response.IsSuccessStatusCode;
    }

    private async Task<List<PreRemoteFile>> GetFileListRecursiveAsync(PreCbftpServer server, string siteName, string remotePath)
    {
        var files = new List<PreRemoteFile>();
        IReadOnlyList<FxpFileItem> items;

        try
        {
            items = await _fxp.BrowseAsync(ToCbftpServer(server), siteName, remotePath);
        }
        catch
        {
            return files;
        }

        foreach (var item in items)
        {
            if (item.IsDirectory)
            {
                var childPath = FxpClientService.CombinePath(remotePath, item.Name);
                var childFiles = await GetFileListRecursiveAsync(server, siteName, childPath);
                files.AddRange(childFiles.Select(f => f with { Name = item.Name + "/" + f.Name }));
            }
            else
            {
                files.Add(new PreRemoteFile(item.Name, item.Size));
            }
        }

        return files;
    }

    /// <summary>
    /// Everything wrong with the destination copy: missing files first, then size
    /// mismatches. Empty means the release arrived intact.
    /// </summary>
    private static List<string> CompareFiles(List<PreRemoteFile> source, List<PreRemoteFile> destination)
    {
        var problems = new List<string>();
        var dest = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in destination)
            dest[f.Name] = f.Size;

        foreach (var src in source)
        {
            if (!dest.TryGetValue(src.Name, out var size))
                problems.Add($"missing: {src.Name}");
            else if (src.Size != size)
                problems.Add($"size differs: {src.Name} ({FxpClientService.FormatSize(size)} vs {FxpClientService.FormatSize(src.Size)})");
        }

        return problems;
    }

    private static PreCbftpServer? FindServer(IReadOnlyList<PreCbftpServer> servers, string? id) =>
        servers.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    private static CbftpServer ToCbftpServer(PreCbftpServer server) => new()
    {
        Id = server.Id,
        Name = server.Name,
        Host = server.Host,
        Port = server.Port,
        Password = server.Password,
        Profile = server.Profile
    };

    private static string BuildEndpoint(PreCbftpServer server)
    {
        var host = server.Host ?? "";
        var port = server.Port ?? "";
        return host.Contains("://", StringComparison.Ordinal)
            ? (host.EndsWith($":{port}", StringComparison.Ordinal) ? host : $"{host}:{port}")
            : $"https://{host}:{port}";
    }

    private static HttpClient CreateClient(PreCbftpServer server, int timeoutSeconds)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        var authBytes = Encoding.UTF8.GetBytes(":" + DecryptPassword(server));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static string DecryptPassword(PreCbftpServer server) =>
        string.IsNullOrWhiteSpace(server.Password) ? "" : SecureConfig.Decrypt(server.Password);

    private static void EnsureDirectory() => Directory.CreateDirectory(ConfigFolder);

    private sealed record PreRemoteFile(string Name, long Size);
}

public sealed class PreCbftpServersConfig
{
    [JsonProperty("cbftp_servers")]
    public List<PreCbftpServer> Servers { get; set; } = new();
}

public sealed class PreSitesConfig
{
    [JsonProperty("sites")]
    public List<PreSiteConfig> Sites { get; set; } = new();
}

public sealed class PreCbftpServer
{
    [JsonProperty("id")]
    public string? Id { get; set; }

    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("host")]
    public string? Host { get; set; }

    [JsonProperty("port")]
    public string? Port { get; set; }

    [JsonProperty("password")]
    public string? Password { get; set; }

    [JsonProperty("profile")]
    public string? Profile { get; set; }
}

public sealed class PreSiteConfig
{
    [JsonProperty("name")]
    public string? Name { get; set; }

    [JsonProperty("cbftp_server_id")]
    public string? CbftpServerId { get; set; }

    [JsonProperty("affil_directory")]
    public string? AffilDirectory { get; set; } = "/pre";

    [JsonProperty("section")]
    public string? Section { get; set; } = "DEFAULT";

    [JsonProperty("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed record PreActionResult(bool Success, string Message, IReadOnlyList<string> Logs)
{
    public static PreActionResult Fail(string message) => new(false, message, new[] { message });
}
