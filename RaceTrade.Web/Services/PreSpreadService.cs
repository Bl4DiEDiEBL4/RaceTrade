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

    public List<PreCbftpServer> ImportServersFromMainConfig(bool save = true)
    {
        var imported = _cbftpStore.Load().CbftpServers
            .Select(s => new PreCbftpServer
            {
                Id = s.Id ?? s.Name ?? Guid.NewGuid().ToString("N")[..8],
                Name = s.Name ?? s.Id ?? "cbftp",
                Host = s.Host ?? "",
                Port = s.Port ?? "",
                Password = s.Password ?? "",
                Profile = s.Profile ?? ""
            })
            .ToList();

        if (save)
            SaveServers(imported);

        return imported;
    }

    public List<PreSiteConfig> LoadSites()
    {
        EnsureDirectory();
        if (!File.Exists(SitesFile))
            return new List<PreSiteConfig>();

        var config = JsonConvert.DeserializeObject<PreSitesConfig>(File.ReadAllText(SitesFile));
        return config?.Sites ?? new List<PreSiteConfig>();
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

                foreach (var site in result.Sites.Where(s => !string.IsNullOrWhiteSpace(s.Name)))
                {
                    if (existingSites.Any(s =>
                            string.Equals(s.Name, site.Name, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(s.CbftpServerId, server.Id, StringComparison.OrdinalIgnoreCase)))
                    {
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

                logs.Add($"{server.Name}: fetched {result.Sites.Count} site(s)");
            }
            catch (Exception ex)
            {
                logs.Add($"{server.Name}: {ex.Message}");
            }
        }

        SaveSites(existingSites);
        return new PreActionResult(imported > 0, imported == 0 ? "No new Pre sites found." : $"Imported {imported} Pre site(s).", logs);
    }

    public async Task<IReadOnlyList<string>> ListReleasesAsync(
        PreCbftpServer server,
        PreSiteConfig sourceSite)
    {
        var items = await _fxp.BrowseAsync(ToCbftpServer(server), sourceSite.Name ?? "", sourceSite.AffilDirectory ?? "/pre");
        return items
            .Where(i => i.IsDirectory && i.Name is not "." and not "..")
            .Select(i => i.Name)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            var ok = CompareFiles(sourceFiles, destinationFiles);
            if (ok)
            {
                complete++;
                logs.Add($"Complete: {site.Name}");
            }
            else
            {
                incomplete++;
                logs.Add($"Incomplete: {site.Name}");
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

    private static bool CompareFiles(List<PreRemoteFile> source, List<PreRemoteFile> destination)
    {
        var dest = destination.ToDictionary(f => f.Name, f => f.Size, StringComparer.OrdinalIgnoreCase);
        foreach (var src in source)
        {
            if (!dest.TryGetValue(src.Name, out var size))
                return false;
            if (src.Size != size)
                return false;
        }

        return true;
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
