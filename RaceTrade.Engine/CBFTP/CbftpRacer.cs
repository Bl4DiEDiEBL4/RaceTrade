using System;
using RaceTrade.Engine.Logging;
using RaceTrade.Engine.Compat;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaceTrade;

public class CbftpJobStats
{
    public string Status { get; set; }
    public string Section { get; set; }
    public int FilesTotal { get; set; }          // not used by stock cbftp (will be 0)
    public int FilesTransferred { get; set; }    // not used by stock cbftp (will be 0)
    public long BytesTransferred { get; set; }   // mapped from size_estimated_bytes
    public double AverageSpeed { get; set; }     // MiB/s
    public bool SpeedFromApi { get; set; }
    public TimeSpan TimeElapsed { get; set; }    // from time_spent_seconds
    public List<string> DestinationSites { get; set; }
    public List<string> Subpaths { get; set; }
}

/// <summary>
/// CBFTP client for initiating spreadjob transfers.
/// </summary>
public class CbftpRacer
{
    private static Dictionary<string, dynamic> CBFTP_CONFIGS = new Dictionary<string, dynamic>();
    // The WinForms build also held a reference to the MainApp form here
    // (SetMainForm/mainForm). It was write-only — never read — and it was the last
    // hard link from the racer to a UI type, so it is gone.

    static CbftpRacer()
    {
        LoadConfiguration();
    }

    private static Color ConvertToDrawingColor(ConsoleColor consoleColor)
    {
        return consoleColor switch
        {
            ConsoleColor.Red => Color.Red,
            ConsoleColor.Green => Color.Green,
            ConsoleColor.Cyan => Color.Cyan,
            ConsoleColor.Yellow => Color.Yellow,
            ConsoleColor.Magenta => Color.Magenta,
            _ => Color.Black,
        };
    }



    public static async Task<CbftpJobStats> GetTransferJobStats(string releaseName)
    {
        try
        {
            var config = CBFTP_CONFIGS.Values.FirstOrDefault();
            if (config == null)
            {
                LogManager.Error("No CBFTP configuration available");
                return null;
            }

            string endpoint;
            if (config.Host.Contains("://"))
            {
                endpoint = config.Host.EndsWith($":{config.Port}")
                    ? config.Host
                    : $"{config.Host}:{config.Port}";
            }
            else
            {
                endpoint = $"https://{config.Host}:{config.Port}";
            }

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using var client = new HttpClient(handler);
            var byteArray = Encoding.ASCII.GetBytes(":" + config.Password);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

            var encodedName = Uri.EscapeDataString(releaseName);

            //  /transferjobs instead of /spreadjobs
            var response = await client.GetAsync($"{endpoint}/transferjobs/{encodedName}");

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"CBFTP API Response for transferjob '{releaseName}': Status={response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                return null;
            }

            var content = await response.Content.ReadAsStringAsync();

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"CBFTP TransferJob '{releaseName}' Response: {content}");
            }

            var jobData = JsonConvert.DeserializeObject<JObject>(content) ?? new JObject();
            var jobStatus = ReadString(jobData, "status");

            var stats = new CbftpJobStats
            {
                Status = string.IsNullOrWhiteSpace(jobStatus) ? "unknown" : jobStatus,
                DestinationSites = new List<string>(),
                Subpaths = new List<string>()
            };

            AddDistinct(stats.DestinationSites, ReadStringList(jobData,
                "dst_site", "dst_sites", "destination_site", "destination_sites", "target_site", "target_sites", "sites"));

            // Files info
            stats.FilesTotal = (int)ReadLong(jobData, "files_total", "filesTotal");
            stats.FilesTransferred = (int)ReadLong(jobData, "files_progress", "files_transferred", "filesTransferred");

            stats.BytesTransferred = ReadLong(jobData,
                "size_progress_bytes", "bytes_transferred", "bytesTransferred", "size_estimated_bytes");

            stats.TimeElapsed = TimeSpan.FromSeconds(ReadLong(jobData, "time_spent_seconds", "timeSpentSeconds"));

            if (stats.TimeElapsed.TotalSeconds > 0 && stats.BytesTransferred > 0)
            {
                stats.AverageSpeed =
                    (stats.BytesTransferred / stats.TimeElapsed.TotalSeconds) / (1024.0 * 1024.0);
            }

            PopulateJobDetails(jobData, stats);

            return stats;
        }
        catch (Exception ex)
        {
            LogManager.Error($"Failed to get transferjob stats for '{releaseName}': {ex.Message}");
            return null;
        }
    }


    public static async Task<string> GetSiteRulesTextAsync(string siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName))
            return "No site name provided.";

        var config = CBFTP_CONFIGS.Values.FirstOrDefault();
        if (config == null)
        {
            LogManager.Error("No CBFTP configuration available for SITE RULES");
            return "No CBFTP configuration available.";
        }

        string host = config.Host;
        string port = config.Port;
        string password = config.Password;
        string serverName = config.Name;
        string endpoint = null;

        try
        {
            bool allowInsecureSsl = EngineSettings.AllowInsecureSsl;

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                {
                    if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                        return true;

                    if (allowInsecureSsl)
                        return true;

                    if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
                        return true;

                    LogManager.Error($"SSL certificate error for SITE RULES: {sslPolicyErrors}");
                    return false;
                }
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            var byteArray = Encoding.ASCII.GetBytes(":" + password);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            if (host.Contains("://"))
                endpoint = host.EndsWith($":{port}") ? host : $"{host}:{port}";
            else
                endpoint = $"https://{host}:{port}";

            var payload = new
            {
                command = "SITE RULES",
                sites = new[] { siteName }
            };

            var json = JsonConvert.SerializeObject(payload, Formatting.None);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"POST {endpoint}/raw payload:\n{JsonConvert.SerializeObject(payload, Formatting.Indented)}");
            }

            var response = await client.PostAsync($"{endpoint}/raw", content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                LogManager.LogCBFTP(
                    CBFTPEventType.Error,
                    $"SITE RULES HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    releaseName: null,
                    targetSite: siteName
                );

                return $"SITE RULES failed ({(int)response.StatusCode} {response.ReasonPhrase})\r\n\r\n{responseText}";
            }

            // --- JSON -> clean text ---------------------------------------
            string rawRules = null;

            try
            {
                dynamic obj = JsonConvert.DeserializeObject<dynamic>(responseText);

                // expect: { failures: [], successes: [ { name: "...", result: "200- ..." } ] }
                if (obj != null && obj.successes != null && obj.successes.Count > 0)
                {
                    var first = obj.successes[0];
                    if (first != null && first.result != null)
                    {
                        rawRules = (string)first.result;
                    }
                }
            }
            catch
            {
                // if JSON parsing fails we just fall back to raw responseText
            }

            if (string.IsNullOrWhiteSpace(rawRules))
            {
                // fallback: show raw (but at least something)
                return responseText;
            }

            // --- strip 200- / 200 prefixes and command footer --------------
            var sb = new StringBuilder();
            var lines = rawRules.Replace("\r\n", "\n").Split('\n');

            foreach (var lineRaw in lines)
            {
                var line = lineRaw;

                // skip final "200 Command Successful." line
                if (line.Contains("Command Successful"))
                    continue;

                // remove common FTP-style prefixes
                if (line.StartsWith("200- "))
                    line = line.Substring(5);
                else if (line.StartsWith("200-"))
                    line = line.Substring(4);
                else if (line.StartsWith("200 "))
                    line = line.Substring(4);
                else if (line.StartsWith("200"))
                    line = line.Substring(3);

                // keep empty lines to preserve spacing between sections
                if (line.Length == 0)
                {
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine(line);
                }
            }

            return sb.ToString().TrimEnd();
        }
        catch (TaskCanceledException)
        {
            LogManager.LogCBFTP(
                CBFTPEventType.Error,
                "SITE RULES request timeout (30 seconds)",
                releaseName: null,
                targetSite: siteName
            );

            return "SITE RULES request timeout (30 seconds).";
        }
        catch (Exception ex)
        {
            LogManager.LogCBFTP(
                CBFTPEventType.Error,
                $"SITE RULES HTTP error: {ex.Message}",
                releaseName: null,
                targetSite: siteName
            );

            return $"Error while requesting SITE RULES:\r\n{ex.Message}";
        }
    }



    /// <summary>
    /// Uses ONLY the stock CBFTP endpoint: GET /spreadjobs/{releaseName}
    /// and only stock fields: status, sites, size_estimated_bytes, time_spent_seconds.
    /// </summary>
    public static async Task<CbftpJobStats> GetJobStats(string releaseName)
    {
        try
        {
            var config = CBFTP_CONFIGS.Values.FirstOrDefault();
            if (config == null)
            {
                LogManager.Error("No CBFTP configuration available");
                return null;
            }

            string endpoint;
            if (config.Host.Contains("://"))
            {
                endpoint = config.Host.EndsWith($":{config.Port}")
                    ? config.Host
                    : $"{config.Host}:{config.Port}";
            }
            else
            {
                endpoint = $"https://{config.Host}:{config.Port}";
            }

            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) => true
            };

            using var client = new HttpClient(handler);
            var byteArray = Encoding.ASCII.GetBytes(":" + config.Password);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));

            var encodedName = Uri.EscapeDataString(releaseName);

            // ONLY the stock endpoint now:
            var response = await client.GetAsync($"{endpoint}/spreadjobs/{encodedName}");

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"CBFTP API Response for job '{releaseName}': Status={response.StatusCode}");
            }

            if (!response.IsSuccessStatusCode)
            {
                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    return null;

                return null;
            }

            var content = await response.Content.ReadAsStringAsync();

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"CBFTP Job '{releaseName}' Response: {content}");
            }

            var jobData = JsonConvert.DeserializeObject<JObject>(content) ?? new JObject();
            var jobStatus = ReadString(jobData, "status");

            var stats = new CbftpJobStats
            {
                Status = string.IsNullOrWhiteSpace(jobStatus) ? "unknown" : jobStatus,
                DestinationSites = new List<string>(),
                Subpaths = new List<string>()
            };

            AddDistinct(stats.DestinationSites, ReadStringList(jobData,
                "sites", "destination_sites", "dst_sites", "target_sites"));

            // size_estimated_bytes (stock CBFTP field)
            long estimatedBytes = ReadLong(jobData, "size_estimated_bytes", "size_bytes", "bytes_total", "bytes");

            // time_spent_seconds (stock CBFTP field)
            long timeSpentSeconds = ReadLong(jobData, "time_spent_seconds", "timeSpentSeconds");

            stats.BytesTransferred = estimatedBytes;
            stats.FilesTransferred = 0; // stock cbftp doesn't give per-job file count here
            stats.FilesTotal = 0;
            stats.TimeElapsed = TimeSpan.FromSeconds(timeSpentSeconds);

            if (stats.TimeElapsed.TotalSeconds > 0 && stats.BytesTransferred > 0)
            {
                stats.AverageSpeed =
                    (stats.BytesTransferred / stats.TimeElapsed.TotalSeconds) / (1024.0 * 1024.0);
            }

            PopulateJobDetails(jobData, stats);

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug(
                    $"Job '{releaseName}': Status={stats.Status}, " +
                    $"Size(est)={FormatSize(stats.BytesTransferred)}, " +
                    $"{SpeedLabel(stats)}={stats.AverageSpeed:F1} MiB/s"
                );
            }

            return stats;
        }
        catch (Exception ex)
        {
            LogManager.Error($"Failed to get job stats for '{releaseName}': {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Monitor stock CBFTP job: only cares about DONE / FAILED / TIMEOUT.
    /// </summary>
    public static async Task MonitorJobProgress(
        string releaseName,
        string announceSite,
        string section,
        List<string> targetSites,
        CancellationToken cancellationToken,
        // Threaded through purely so the Completed/Failed lines carry the same IRC
        // channel as the Detected/Racing lines they belong to. Without it those two
        // rows sit in the log with an empty channel column and cannot be tied back
        // to the announce.
        string ircChannel = null)
    {
        try
        {
            int checkCount = 0;

            while (!cancellationToken.IsCancellationRequested)
            {
                var stats = await GetJobStats(releaseName);

                if (stats == null)
                {
                    // Job not found - if we've checked a few times, assume it's done
                    checkCount++;
                    if (checkCount > 3)
                    {
                        if (EngineSettings.DebugEnabled)
                        {
                            LogManager.Debug($"Job '{releaseName}' not found after {checkCount} checks - assuming completed");
                        }
                        break;
                    }
                    await Task.Delay(5000, cancellationToken);
                    continue;
                }

                checkCount = 0;
                string status = (stats.Status ?? "").ToUpperInvariant();

                if (status == "DONE")
                {
                    var allSites = string.Join(",", targetSites);
                    string completionMsg = BuildJobLogMessage(stats, section, targetSites, "✓");

                    LogManager.LogCBFTP(
                        CBFTPEventType.SpreadJobCompleted,
                        completionMsg,
                        releaseName: releaseName,
                        targetSite: allSites
                    );

                    // The race log is where you look to see whether a race finished, so
                    // this belongs there and not only in the CBFTP log.
                    LogManager.LogRace(
                        RaceStatus.Completed,
                        releaseName,
                        announceSite,   // origin/winner
                        targetSite: allSites,
                        quality: section,
                        ircChannel: ircChannel
                    );
                    break;
                }
                else if (status == "FAILED" || status == "TIMEOUT" || status == "ABORTED")
                {
                    string reason = status == "TIMEOUT" ? "CBFTP transfer timeout"
                                  : status == "ABORTED" ? "CBFTP transfer aborted"
                                  : "CBFTP transfer failed";

                    string failMsg = BuildJobLogMessage(stats, section, targetSites, $"✗ {reason}");

                    LogManager.LogCBFTP(
                        CBFTPEventType.SpreadJobFailed,
                        failMsg,
                        releaseName: releaseName
                    );

                    var allSites = string.Join(",", targetSites);

                    LogManager.LogRace(
                        RaceStatus.Failed,
                        releaseName,
                        announceSite,
                        targetSite: allSites,
                        quality: section,
                        filterReason: reason,
                        ircChannel: ircChannel
                    );
                    break;
                }

                // Keep polling every 5 seconds while in-progress
                await Task.Delay(5000, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error monitoring job '{releaseName}': {ex.Message}");
        }
    }

    private static string BuildJobLogMessage(CbftpJobStats stats, string fallbackSection, IEnumerable<string> fallbackSites, string prefix)
    {
        var parts = new List<string>();
        var section = !string.IsNullOrWhiteSpace(stats.Section) ? stats.Section : fallbackSection;
        var sites = stats.DestinationSites?.Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new List<string>();
        if (sites.Count == 0 && fallbackSites != null)
            sites = fallbackSites.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();

        if (!string.IsNullOrWhiteSpace(section))
            parts.Add($"Section: {section}");

        if (sites.Count > 0)
            parts.Add($"Sites: {string.Join(",", sites)}");

        if (!string.IsNullOrWhiteSpace(stats.Status))
            parts.Add($"Status: {FormatStatus(stats.Status)}");

        if (stats.Subpaths?.Count > 0)
            parts.Add($"Subpaths: {string.Join(", ", stats.Subpaths)}");

        var metrics = new List<string>();
        if (stats.BytesTransferred > 0)
            metrics.Add($"Size(est): {FormatSize(stats.BytesTransferred)}");

        if (stats.AverageSpeed > 0)
            metrics.Add($"{SpeedLabel(stats)}: {stats.AverageSpeed:F1} MiB/s");

        if (stats.TimeElapsed.TotalSeconds > 0)
            metrics.Add($"Time: {stats.TimeElapsed:mm\\:ss}");

        if (metrics.Count > 0)
            parts.Add(string.Join(" | ", metrics));

        return parts.Count == 0 ? prefix : $"{prefix} {string.Join(" | ", parts)}";
    }

    private static void PopulateJobDetails(JObject jobData, CbftpJobStats stats)
    {
        stats.Section = ReadString(jobData,
            "section", "cbftp_section", "cbftpSection", "src_section", "dst_section", "section_name", "category");

        stats.Subpaths = ReadStringList(jobData,
            "subpaths", "sub_paths", "paths", "path_groups", "file_subpaths", "fileSubpaths");

        if (TryReadApiSpeedMiBps(jobData, out var apiSpeed))
        {
            stats.AverageSpeed = apiSpeed;
            stats.SpeedFromApi = true;
        }
    }

    private static string SpeedLabel(CbftpJobStats stats) =>
        stats.SpeedFromApi ? "Speed" : "Avg(est)";

    private static string FormatStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)) return "";

        return status.Equals("DONE", StringComparison.OrdinalIgnoreCase)
            ? "Done"
            : status.Equals("TIMEOUT", StringComparison.OrdinalIgnoreCase)
                ? "Timeout"
                : status.Equals("ABORTED", StringComparison.OrdinalIgnoreCase)
                    ? "Aborted"
                    : status.Equals("FAILED", StringComparison.OrdinalIgnoreCase)
                        ? "Failed"
                        : status;
    }

    private static JToken FindToken(JObject data, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = data.Properties()
                .FirstOrDefault(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (prop?.Value != null && prop.Value.Type != JTokenType.Null)
                return prop.Value;
        }

        return null;
    }

    private static string ReadString(JObject data, params string[] names)
    {
        var token = FindToken(data, names);
        return token == null ? "" : token.ToString();
    }

    private static long ReadLong(JObject data, params string[] names)
    {
        var token = FindToken(data, names);
        if (token == null) return 0;

        try { return token.Value<long>(); }
        catch
        {
            return long.TryParse(token.ToString(), out var value) ? value : 0;
        }
    }

    private static bool TryReadDouble(JObject data, out double value, params string[] names)
    {
        value = 0;
        var token = FindToken(data, names);
        if (token == null) return false;

        try
        {
            value = token.Value<double>();
            return true;
        }
        catch
        {
            return double.TryParse(token.ToString(), out value);
        }
    }

    private static bool TryReadApiSpeedMiBps(JObject data, out double value)
    {
        if (TryReadDouble(data, out var bytesPerSecond,
                "speed_bytes_per_second", "average_speed_bytes_per_second", "avg_speed_bytes_per_second",
                "bytes_per_second", "speed_bps"))
        {
            value = bytesPerSecond / (1024.0 * 1024.0);
            return true;
        }

        if (TryReadDouble(data, out value,
                "speed_mib_per_second", "average_speed_mib_per_second", "avg_speed_mib_per_second",
                "speed_mibps", "average_speed_mibps"))
        {
            return true;
        }

        value = 0;
        return false;
    }

    private static List<string> ReadStringList(JObject data, params string[] names)
    {
        var token = FindToken(data, names);
        if (token == null) return new List<string>();

        if (token is JArray array)
        {
            return array
                .Select(FormatListItem)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        if (token is JObject obj)
        {
            return obj.Properties()
                .Select(p => $"{p.Name} {p.Value}".Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }

        var raw = token.ToString();
        return raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList();
    }

    private static string FormatListItem(JToken token)
    {
        if (token is JObject obj)
        {
            var name = ReadString(obj, "path", "subpath", "name", "dir", "directory");
            var files = ReadString(obj, "files", "file_count", "files_total", "count");
            var note = ReadString(obj, "note", "type", "kind");

            if (!string.IsNullOrWhiteSpace(name))
            {
                var suffix = string.Join("/", new[] { files, note }.Where(s => !string.IsNullOrWhiteSpace(s)));
                return string.IsNullOrWhiteSpace(suffix) ? name : $"{name} ({suffix})";
            }
        }

        return token.ToString();
    }

    private static void AddDistinct(List<string> target, IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !target.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(value);
            }
        }
    }

    // Helper: size formatting
    private static string FormatSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Re-reads cbftp/cbftp_config.json. Call after servers are added/edited/deleted
    /// in the UI so racing does not keep using a stale server list until restart.
    /// </summary>
    public static void ReloadConfiguration()
    {
        CBFTP_CONFIGS.Clear();
        LoadConfiguration();
    }

    private static void LoadConfiguration()
    {
        const string mainConfigPath = "cbftp/cbftp_config.json";

        try
        {
            if (File.Exists(mainConfigPath))
            {
                var jsonContent = File.ReadAllText(mainConfigPath);
                var config = JsonConvert.DeserializeObject<MainConfig>(jsonContent);

                if (config?.CbftpServers == null)
                {
                    LogManager.Error("No CBFTP servers found in configuration");
                    return;
                }

                foreach (var server in config.CbftpServers)
                {
                    CBFTP_CONFIGS[server.Id] = new
                    {
                        Name = server.Name ?? server.Id,
                        Host = server.Host,
                        Port = server.Port,
                        Password = SecureConfig.Decrypt(server.Password),
                        Profile = server.Profile
                    };

                    LogManager.LogCBFTP(
                        CBFTPEventType.Connected,
                        $"Loaded config: {server.Name ?? server.Id}, Host: {server.Host}, Port: {server.Port}, Profile: {server.Profile}"
                    );
                }

                LogManager.Info($"Loaded {config.CbftpServers.Count} CBFTP configuration(s)");
            }
            else
            {
                LogManager.Error($"Main configuration file not found: {mainConfigPath}");
            }
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error loading CBFTP configurations: {ex.Message}");
        }
    }

    // One HttpClient per cbftp endpoint, kept alive for the process lifetime.
    // Creating a client per race meant every single race paid a TCP handshake plus
    // a full TLS handshake before the command bytes could leave — pure lost race
    // time. Reusing the client keeps the connection warm (keep-alive) so a race
    // command usually goes out in a single round trip.
    private static readonly ConcurrentDictionary<string, HttpClient> CbftpClients =
        new ConcurrentDictionary<string, HttpClient>();

    // Long enough that a healthy-but-busy cbftp still answers (avoiding a fallback
    // to the next server, which would submit the same spreadjob twice), short
    // enough that a dead cbftp doesn't hold the race hostage for half a minute.
    private const int SpreadjobTimeoutSeconds = 8;

    private static HttpClient GetCbftpClient(string endpoint, string password, string host)
    {
        var key = endpoint + "\n" + password;
        return CbftpClients.GetOrAdd(key, _ =>
        {
            // Note: the callback reads EngineSettings.AllowInsecureSsl live rather than
            // capturing it, so toggling the setting takes effect without having to
            // rebuild the cached client.
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                {
                    if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                        return true;

                    if (EngineSettings.AllowInsecureSsl)
                    {
                        if (EngineSettings.DebugEnabled)
                        {
                            LogManager.Warning($"Insecure SSL allowed: {sslPolicyErrors} (cert: {cert?.Subject})");
                        }
                        return true;
                    }

                    if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors)
                    {
                        if (EngineSettings.DebugEnabled)
                        {
                            LogManager.Warning($"Accepting self-signed certificate from {host}");
                        }
                        return true;
                    }

                    LogManager.Error($"SSL certificate error: {sslPolicyErrors}");
                    return false;
                }
            };

            var c = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(SpreadjobTimeoutSeconds)
            };

            var byteArray = Encoding.ASCII.GetBytes(":" + password);
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            c.DefaultRequestHeaders.ConnectionClose = false;

            return c;
        });
    }

    /// <summary>
    /// Starts a spreadjob transfer on CBFTP.
    /// </summary>
    public static async Task<TransferResult> StartSpreadjobTransfer(
        Dictionary<string, object> payload,
        string host,
        string port,
        string password,
        string serverName,
        string release,
        string section)
    {
        string endpoint = null;

        try
        {
            if (host.Contains("://"))
            {
                endpoint = host.EndsWith($":{port}") ? host : $"{host}:{port}";
            }
            else
            {
                endpoint = $"https://{host}:{port}";
            }

            // Reused, connection-warm client (see GetCbftpClient).
            var client = GetCbftpClient(endpoint, password, host);

            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"Sending payload to {endpoint}/spreadjobs:\n{JsonConvert.SerializeObject(payload, Formatting.Indented)}");
            }

            var response = await client.PostAsync($"{endpoint}/spreadjobs", content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                LogManager.LogCBFTP(
                    CBFTPEventType.SpreadJobFailed,
                    $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    releaseName: release,
                    targetSite: serverName
                );

                return TransferResult.Failed(
                    endpoint,
                    $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    (int)response.StatusCode,
                    responseText
                );
            }

            try
            {
                var jsonResponse = JsonConvert.DeserializeObject<Dictionary<string, object>>(responseText);

                // The cbftp API doc does not document the POST /spreadjobs response body.
                // Treat any 2xx as started unless the response explicitly reports a
                // non-STARTED state — otherwise a working job would be logged as failed
                // and the next cbftp server would be tried (duplicate job).
                string state = jsonResponse != null && jsonResponse.ContainsKey("state")
                    ? jsonResponse["state"]?.ToString()
                    : null;

                bool started = state == null ||
                               state.Equals("STARTED", StringComparison.OrdinalIgnoreCase);

                if (started)
                {
                    int? jobId = null;
                    if (jsonResponse.ContainsKey("id") && int.TryParse(jsonResponse["id"].ToString(), out int id))
                    {
                        jobId = id;
                    }

                    LogManager.LogCBFTP(
                        CBFTPEventType.SpreadJobStarted,
                        "Spreadjob started successfully",
                        spreadJobId: jobId,
                        releaseName: release,
                        targetSite: serverName
                    );

                    return TransferResult.Successful(endpoint, responseText, jobId);
                }
                else
                {
                    LogManager.LogCBFTP(
                        CBFTPEventType.SpreadJobFailed,
                        $"Spreadjob not started. State: {state}",
                        releaseName: release,
                        targetSite: serverName
                    );

                    return TransferResult.Failed(
                        endpoint,
                        $"Spreadjob not started. State: {state}",
                        (int)response.StatusCode,
                        responseText
                    );
                }
            }
            catch (JsonException)
            {
                LogManager.LogCBFTP(
                    CBFTPEventType.Error,
                    "Unexpected response format",
                    releaseName: release,
                    targetSite: serverName
                );

                return TransferResult.Failed(
                    endpoint,
                    "Unexpected response format",
                    (int)response.StatusCode,
                    responseText
                );
            }
        }
        catch (TaskCanceledException)
        {
            LogManager.LogCBFTP(
                CBFTPEventType.Error,
                $"Request timeout ({SpreadjobTimeoutSeconds} seconds)",
                releaseName: release,
                targetSite: serverName
            );

            return TransferResult.Failed(endpoint ?? host, $"Request timeout ({SpreadjobTimeoutSeconds} seconds)");
        }
        catch (HttpRequestException ex)
        {
            LogManager.LogCBFTP(
                CBFTPEventType.Error,
                $"HTTP request failed: {ex.Message}",
                releaseName: release,
                targetSite: serverName
            );

            return TransferResult.FromException(endpoint ?? host, ex);
        }
        catch (Exception ex)
        {
            LogManager.LogCBFTP(
                CBFTPEventType.Error,
                $"Unexpected error: {ex.Message}",
                releaseName: release,
                targetSite: serverName
            );

            return TransferResult.FromException(endpoint ?? host, ex);
        }
    }

    /// <summary>
    /// Handles a transfer job with proper error handling.
    /// </summary>
    public static async Task HandleTransferJob(string section, string release, FilterResult filterResult, string announceSite, string ircChannel = null)
    {
        if (EngineSettings.DebugEnabled)
        {
            LogManager.Debug($"Starting transfer job for section: {section}, release: {release}");
        }

        if (filterResult == null)
        {
            LogManager.Error($"filterResult is null for release '{release}'");
            return;
        }

        if (filterResult.Status != FilterStatus.Success)
        {
            if (EngineSettings.DebugEnabled)
            {
                LogManager.Warning($"Release '{release}': {filterResult.Message}");
            }
            return;
        }

        var allowedSites = filterResult.AllowedSites;

        if (allowedSites == null || allowedSites.Count < 2)
        {
            if (EngineSettings.DebugEnabled)
            {
                LogManager.Warning($"Insufficient sites for '{release}': {allowedSites?.Count ?? 0}");
            }
            return;
        }

        try
        {
            // Extract group from release name
            string group = RaceHelper.ExtractGroupFromRelease(release);

            var sitesDlOnly = new List<string>();

            foreach (var site in allowedSites)
            {
                if (!SiteConfigManager.TryGetSiteConfig(site, out var siteConfig))
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Warning($"Could not load settings for site '{site}'");
                    }
                    continue;
                }

                // Check if site is manually set as dl_only
                if (siteConfig.SiteSettings?.DlOnlySite == true)
                {
                    if (!sitesDlOnly.Contains(site))
                    {
                        sitesDlOnly.Add(site);
                        if (EngineSettings.DebugEnabled)
                        {
                            LogManager.Info($"Site '{site}' is manually set as dl_only_site");
                        }
                    }
                }

                // Check if group is in site's affils list
                if (!string.IsNullOrEmpty(group) &&
                    siteConfig.Affils != null &&
                    siteConfig.Affils.Contains(group, StringComparer.OrdinalIgnoreCase))
                {
                    if (!sitesDlOnly.Contains(site))
                    {
                        sitesDlOnly.Add(site);
                        LogManager.Success($"[{site}] is affil for group [{group}], adding to download-only");
                    }
                }
            }

            bool anyTransferSucceeded = false;

            foreach (var cbftpKey in CBFTP_CONFIGS.Keys)
            {
                var config = CBFTP_CONFIGS[cbftpKey];
                string serverName = config.Name;
                string host = config.Host;
                string port = config.Port;
                string password = config.Password;

                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port) || string.IsNullOrEmpty(password))
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Warning($"CBFTP '{cbftpKey}': Missing configuration");
                    }
                    continue;
                }

                var payload = new Dictionary<string, object>
                {
                    { "section", section },
                    { "name", release },
                    { "sites", allowedSites },
                    { "profile", config.Profile }
                };

                // Add sites_dlonly array (not string)
                if (sitesDlOnly.Any())
                {
                    payload.Add("sites_dlonly", sitesDlOnly);

                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Debug($"sites_dlonly: [{string.Join(", ", sitesDlOnly)}]");
                    }
                }

                LogManager.LogCBFTP(
                    CBFTPEventType.SpreadJobSent,
                    "Sending spreadjob",
                    releaseName: release,
                    targetSite: serverName
                );

                var transferResult = await StartSpreadjobTransfer(payload, host, port, password, serverName, release, section);

                if (transferResult.Success)
                {
                    anyTransferSucceeded = true;

                    if (transferResult.JobId.HasValue)
                    {
                        var cts = new CancellationTokenSource();
                        _ = Task.Run(() => MonitorJobProgress(
                            release,
                            announceSite,
                            section,
                            allowedSites,
                            cts.Token,
                            ircChannel
                        ));
                    }

                    if (!transferResult.JobId.HasValue)
                    {
                        LogManager.LogCBFTP(
                            CBFTPEventType.SpreadJobCompleted,
                            "Transfer completed successfully",
                            releaseName: release,
                            targetSite: serverName
                        );
                    }

                    // Store the PLAIN release name — IsReleaseProcessedAsync does an exact
                    // match on it, so a decorated string kills duplicate detection entirely
                    // (re-announces would be raced again). Timestamp/section/sites have
                    // their own columns.
                    SQLiteHelper.LogProcessedRelease(
                        releaseName: release,
                        category: section,
                        siteName: string.Join(",", allowedSites),
                        dateProcessed: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        pretime: 0
                    );

                    break;
                }
                else
                {
                    LogManager.LogCBFTP(
                        CBFTPEventType.SpreadJobFailed,
                        $"Transfer failed: {transferResult.ErrorMessage}",
                        releaseName: release,
                        targetSite: serverName
                    );
                }
            }

            if (!anyTransferSucceeded)
            {
                LogManager.Error($"All CBFTP servers failed for release '{release}'");
            }
        }
        catch (Exception ex)
        {
            LogManager.Exception(ex, $"Exception in HandleTransferJob for '{release}'");
        }
    }
    
    // ============================================================
    //  TRANSFERJOBS (FXP / DOWNLOAD / UPLOAD) – used for requests
    // ============================================================

    /// <summary>
    /// Low-level HTTP helper that POSTS to /transferjobs on cbftp.
    /// </summary>
    private static async Task<TransferResult> PostTransferJob(
        Dictionary<string, object> payload,
        string host,
        string port,
        string password,
        string serverName,
        string releaseName)
    {
        string endpoint = null;

        try
        {
            bool allowInsecureSsl = EngineSettings.AllowInsecureSsl;

            using var handler = new HttpClientHandler
            {
                // cbftp uses self-signed cert; this is equivalent to curl -k
                ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                {
                    if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                        return true;

                    if (allowInsecureSsl)
                        return true;

                    return false;
                }
            };

            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(30)
            };

            var byteArray = Encoding.ASCII.GetBytes(":" + password);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
            client.DefaultRequestHeaders.Accept.Add(
                new MediaTypeWithQualityHeaderValue("application/json"));

            if (host.Contains("://"))
                endpoint = host.EndsWith($":{port}") ? host : $"{host}:{port}";
            else
                endpoint = $"https://{host}:{port}";

            var json = JsonConvert.SerializeObject(payload, Formatting.None);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"POST {endpoint}/transferjobs payload:\n" +
                                 JsonConvert.SerializeObject(payload, Formatting.Indented));
            }

            var response = await client.PostAsync($"{endpoint}/transferjobs", content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                LogManager.LogCBFTP(
                    CBFTPEventType.SpreadJobFailed,
                    $"Transferjob HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    releaseName: releaseName,
                    targetSite: serverName
                );

                return TransferResult.Failed(
                    endpoint,
                    $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                    (int)response.StatusCode,
                    responseText
                );
            }

            // API doc doesn’t define a special "state" for transferjobs;
            // any 2xx here is treated as successfully started.
            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"Transferjob response for {releaseName}:\n{responseText}");
            }

            return TransferResult.Successful(endpoint, responseText, null);
        }
        catch (TaskCanceledException)
        {
            LogManager.LogCBFTP(
                CBFTPEventType.Error,
                "Transferjob request timeout (30 seconds)",
                releaseName: releaseName,
                targetSite: serverName
            );

            return TransferResult.Failed(endpoint ?? host, "Transferjob request timeout (30 seconds)");
        }
        catch (Exception ex)
        {
            LogManager.LogCBFTP(
                CBFTPEventType.Error,
                $"Transferjob HTTP error: {ex.Message}",
                releaseName: releaseName,
                targetSite: serverName
            );

            return TransferResult.FromException(endpoint ?? host, ex);
        }
    }


    /// <summary>
    /// Starts a transfer job (FXP) for a single release:
    ///   src_site  ->  dst_site:dst_path
    ///
    /// srcSectionOrPath:
    ///   - if srcIsSection == true  => sent as "src_section"
    ///   - if srcIsSection == false => sent as "src_path"
    ///
    /// dstPath:
    ///   - sent as "dst_path" (can be a real path or a cbftp section name).
    /// </summary>
    public static async Task<TransferResult> StartTransferJobFxp(
        string srcSite,
        string srcSectionOrPath,
        bool srcIsSection,
        string dstSite,
        string dstPath,
        string releaseName)
    {
        // pick the first cbftp config (same as GetJobStats)
        var config = CBFTP_CONFIGS.Values.FirstOrDefault();
        if (config == null)
        {
            LogManager.Error("No CBFTP configuration available for transferjob");
            return TransferResult.Failed("NO_CONFIG", "No CBFTP configuration available");
        }

        return await StartTransferJobFxp(
            srcSite, srcSectionOrPath, srcIsSection,
            dstSite, dstPath, releaseName,
            (string)config.Host, (string)config.Port, (string)config.Password, (string)config.Name);
    }

    /// <summary>
    /// Same as above but posts the FXP job to an explicitly given cbftp instance
    /// (host/port/password) instead of the first one from cbftp_config.json.
    /// Used by the Pre manager, which keeps its own cbftp server list.
    /// The given instance must know BOTH srcSite and dstSite.
    /// </summary>
    public static async Task<TransferResult> StartTransferJobFxp(
        string srcSite,
        string srcSectionOrPath,
        bool srcIsSection,
        string dstSite,
        string dstPath,
        string releaseName,
        string host,
        string port,
        string password,
        string serverName)
    {
        // Build payload exactly as in cbftp docs for an FXP job
        var payload = new Dictionary<string, object>
        {
            { "src_site", srcSite },
            { "dst_site", dstSite },
            { "name",     releaseName }
        };

        if (srcIsSection)
            payload["src_section"] = srcSectionOrPath;
        else
            payload["src_path"] = srcSectionOrPath;

        // destination: we always use dst_path for request fills
        payload["dst_path"] = dstPath;

        return await PostTransferJob(payload, host, port, password, serverName, releaseName);
    }

    /// <summary>
    /// Convenience wrapper used by the RequestAutoFill logic.
    /// Returns true on success, false on failure.
    /// </summary>
    public static async Task<bool> StartRequestTransferJob(
        string srcSite,
        string dstSite,
        string dstPath,
        string releaseName,
        string srcSectionOrPath,
        bool srcIsSection)
    {
        var result = await StartTransferJobFxp(
            srcSite,
            srcSectionOrPath,
            srcIsSection,
            dstSite,
            dstPath,
            releaseName);

        if (!result.Success)
        {
            LogManager.LogCBFTP(
                CBFTPEventType.SpreadJobFailed,
                $"Request transferjob failed: {result.ErrorMessage}",
                releaseName: releaseName,
                targetSite: dstSite
            );
        }

        return result.Success;
    }






    // Configuration classes
    public class MainConfig
    {
        [JsonProperty("cbftp_servers")]
        public List<CbftpServer> CbftpServers { get; set; }

        [JsonProperty("jobs")]
        public JobSettings Jobs { get; set; }
    }

    public class CbftpServer
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("port")]
        public string Port { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        [JsonProperty("profile")]
        public string Profile { get; set; }
    }

    public class JobSettings
    {
        [JsonProperty("spreadjob")]
        public bool Spreadjob { get; set; }

        [JsonProperty("fxpjob")]
        public bool Fxpjob { get; set; }
    }
}
