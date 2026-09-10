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
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaceTrade;

public class FxpBackendJobStats
{
    public string Status { get; set; }
    public string Section { get; set; }
    public int FilesTotal { get; set; }          // not used by stock FXP backend (will be 0)
    public int FilesTransferred { get; set; }    // not used by stock FXP backend (will be 0)
    public long BytesTransferred { get; set; }   // mapped from size_estimated_bytes
    public double AverageSpeed { get; set; }     // MiB/s
    public bool SpeedFromApi { get; set; }
    public TimeSpan TimeElapsed { get; set; }    // from time_spent_seconds
    public List<string> DestinationSites { get; set; }
    public List<string> Subpaths { get; set; }
}

/// <summary>
/// FXP backend client for initiating spreadjob transfers.
/// </summary>
public class FxpBackendRacer
{
    private static Dictionary<string, dynamic> FXP_BACKEND_CONFIGS = new Dictionary<string, dynamic>();
    // The WinForms build also held a reference to the MainApp form here
    // (SetMainForm/mainForm). It was write-only — never read — and it was the last
    // hard link from the racer to a UI type, so it is gone.

    static FxpBackendRacer()
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



    public static async Task<FxpBackendJobStats> GetTransferJobStats(string releaseName)
    {
        try
        {
            var config = FXP_BACKEND_CONFIGS.Values.FirstOrDefault();
            if (config == null)
            {
                LogManager.Error("No FXP backend configuration available");
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
                LogManager.Debug($"FXP backend API Response for transferjob '{releaseName}': Status={response.StatusCode}");
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
                LogManager.Debug($"FXP backend TransferJob '{releaseName}' Response: {content}");
            }

            var jobData = JsonConvert.DeserializeObject<JObject>(content) ?? new JObject();
            var jobStatus = ReadString(jobData, "status");

            var stats = new FxpBackendJobStats
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


    public static async Task<string> GetSiteRulesTextAsync(string siteName, string fxpBackendServerId = null)
    {
        if (string.IsNullOrWhiteSpace(siteName))
            return "No site name provided.";

        dynamic config = null;
        if (!string.IsNullOrWhiteSpace(fxpBackendServerId))
        {
            if (!FXP_BACKEND_CONFIGS.TryGetValue(fxpBackendServerId, out config))
            {
                foreach (var candidate in FXP_BACKEND_CONFIGS.Values)
                {
                    string candidateName = candidate.Name;
                    if (string.Equals(candidateName, fxpBackendServerId, StringComparison.OrdinalIgnoreCase))
                    {
                        config = candidate;
                        break;
                    }
                }
            }

            if (config == null)
            {
                LogManager.Error($"FXP backend server '{fxpBackendServerId}' is not available for SITE RULES");
                return $"Configured FXP backend server '{fxpBackendServerId}' is not available.";
            }
        }
        else
        {
            config = FXP_BACKEND_CONFIGS.Values.FirstOrDefault();
        }

        if (config == null)
        {
            LogManager.Error("No FXP backend configuration available for SITE RULES");
            return "No FXP backend configuration available.";
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
                LogManager.LogFxpBackend(
                    FxpBackendEventType.Error,
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
            LogManager.LogFxpBackend(
                FxpBackendEventType.Error,
                "SITE RULES request timeout (30 seconds)",
                releaseName: null,
                targetSite: siteName
            );

            return "SITE RULES request timeout (30 seconds).";
        }
        catch (Exception ex)
        {
            LogManager.LogFxpBackend(
                FxpBackendEventType.Error,
                $"SITE RULES HTTP error: {ex.Message}",
                releaseName: null,
                targetSite: siteName
            );

            return $"Error while requesting SITE RULES:\r\n{ex.Message}";
        }
    }



    /// <summary>
    /// Uses ONLY the stock FXP backend endpoint: GET /spreadjobs/{releaseName}
    /// and only stock fields: status, sites, size_estimated_bytes, time_spent_seconds.
    /// </summary>
    public static async Task<FxpBackendJobStats> GetJobStats(string releaseName)
    {
        try
        {
            var config = FXP_BACKEND_CONFIGS.Values.FirstOrDefault();
            if (config == null)
            {
                LogManager.Error("No FXP backend configuration available");
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
                LogManager.Debug($"FXP backend API Response for job '{releaseName}': Status={response.StatusCode}");
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
                LogManager.Debug($"FXP backend Job '{releaseName}' Response: {content}");
            }

            var jobData = JsonConvert.DeserializeObject<JObject>(content) ?? new JObject();
            var jobStatus = ReadString(jobData, "status");

            var stats = new FxpBackendJobStats
            {
                Status = string.IsNullOrWhiteSpace(jobStatus) ? "unknown" : jobStatus,
                DestinationSites = new List<string>(),
                Subpaths = new List<string>()
            };

            AddDistinct(stats.DestinationSites, ReadStringList(jobData,
                "sites", "destination_sites", "dst_sites", "target_sites"));

            // size_estimated_bytes (stock FXP backend field)
            long estimatedBytes = ReadLong(jobData, "size_estimated_bytes", "size_bytes", "bytes_total", "bytes");

            // time_spent_seconds (stock FXP backend field)
            long timeSpentSeconds = ReadLong(jobData, "time_spent_seconds", "timeSpentSeconds");

            stats.BytesTransferred = estimatedBytes;
            stats.FilesTotal = (int)ReadLong(jobData,
                "files_total", "filesTotal", "file_count", "files", "files_count", "total_files");
            stats.FilesTransferred = (int)ReadLong(jobData,
                "files_progress", "files_transferred", "filesTransferred", "files_done", "filesDone", "done_files");
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
    /// Monitor stock FXP backend job: only cares about DONE / FAILED / TIMEOUT.
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
        string ircChannel = null,
        string fxpBackendHost = null,
        string fxpBackendPort = null,
        string fxpBackendPassword = null,
        string fxpBackendServerName = null)
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

                    LogManager.LogFxpBackend(
                        FxpBackendEventType.SpreadJobCompleted,
                        completionMsg,
                        releaseName: releaseName,
                        targetSite: allSites
                    );

                    // The race log is where you look to see whether a race finished, so
                    // this belongs there and not only in the FXP backend log.
                    LogManager.LogRace(
                        RaceStatus.Completed,
                        releaseName,
                        announceSite,   // origin/winner
                        targetSite: allSites,
                        quality: section,
                        ircChannel: ircChannel,
                        details: BuildRaceJobDetails(stats)
                    );
                    break;
                }
                else if (status == "FAILED" || status == "TIMEOUT" || status == "ABORTED")
                {
                    string reason = status == "TIMEOUT" ? "FXP backend transfer timeout"
                                  : status == "ABORTED" ? "FXP backend transfer aborted"
                                  : "FXP backend transfer failed";

                    string failMsg = BuildJobLogMessage(stats, section, targetSites, $"✗ {reason}");

                    LogManager.LogFxpBackend(
                        FxpBackendEventType.SpreadJobFailed,
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
                        ircChannel: ircChannel,
                        details: BuildRaceJobDetails(stats)
                    );

                    await TryHardResetSpreadJobAsync(
                        fxpBackendHost,
                        fxpBackendPort,
                        fxpBackendPassword,
                        fxpBackendServerName,
                        releaseName,
                        reason,
                        cancellationToken
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

    private static string BuildJobLogMessage(FxpBackendJobStats stats, string fallbackSection, IEnumerable<string> fallbackSites, string prefix)
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

        var metrics = BuildJobMetrics(stats);

        if (metrics.Count > 0)
            parts.Add(string.Join(" | ", metrics));

        return parts.Count == 0 ? prefix : $"{prefix} {string.Join(" | ", parts)}";
    }

    private static string BuildRaceJobDetails(FxpBackendJobStats stats)
    {
        var parts = new List<string>();

        if (stats.Subpaths?.Count > 0)
            parts.Add($"Subpaths: {string.Join(", ", stats.Subpaths)}");

        parts.AddRange(BuildJobMetrics(stats));

        return string.Join(" | ", parts);
    }

    private static List<string> BuildJobMetrics(FxpBackendJobStats stats)
    {
        var metrics = new List<string>();

        if (stats.FilesTotal > 0)
        {
            metrics.Add(stats.FilesTransferred > 0 && stats.FilesTransferred != stats.FilesTotal
                ? $"Files: {stats.FilesTransferred}/{stats.FilesTotal}"
                : $"Files: {stats.FilesTotal}");
        }

        if (stats.BytesTransferred > 0)
            metrics.Add($"Size(est): {FormatSize(stats.BytesTransferred)}");

        if (stats.AverageSpeed > 0)
            metrics.Add($"{SpeedLabel(stats)}: {stats.AverageSpeed:F1} MiB/s");

        if (stats.TimeElapsed.TotalSeconds > 0)
            metrics.Add($"Time: {stats.TimeElapsed:mm\\:ss}");

        return metrics;
    }

    private static void PopulateJobDetails(JObject jobData, FxpBackendJobStats stats)
    {
        stats.Section = ReadString(jobData,
            "section", "fxp_backend_section", "fxpBackendSection", "src_section", "dst_section", "section_name", "category");

        stats.Subpaths = ReadStringList(jobData,
            "subpaths", "sub_paths", "paths", "path_groups", "file_subpaths", "fileSubpaths");

        if (stats.FilesTotal <= 0)
            stats.FilesTotal = InferFileCountFromSubpaths(stats.Subpaths);

        if (stats.FilesTransferred <= 0 &&
            stats.FilesTotal > 0 &&
            stats.Status?.Equals("DONE", StringComparison.OrdinalIgnoreCase) == true)
        {
            stats.FilesTransferred = stats.FilesTotal;
        }

        if (TryReadApiSpeedMiBps(jobData, out var apiSpeed))
        {
            stats.AverageSpeed = apiSpeed;
            stats.SpeedFromApi = true;
        }
    }

    private static int InferFileCountFromSubpaths(IEnumerable<string> subpaths)
    {
        var total = 0;

        foreach (var subpath in subpaths ?? Enumerable.Empty<string>())
        {
            foreach (Match match in Regex.Matches(subpath, @"(?<!\w)(\d+)\s*f\b", RegexOptions.IgnoreCase))
            {
                if (int.TryParse(match.Groups[1].Value, out var count))
                    total += count;
            }
        }

        return total;
    }

    private static string SpeedLabel(FxpBackendJobStats stats) =>
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
    /// Re-reads the FXP backend config. Call after servers are added/edited/deleted
    /// in the UI so racing does not keep using a stale server list until restart.
    /// </summary>
    public static void ReloadConfiguration()
    {
        FXP_BACKEND_CONFIGS.Clear();
        LoadConfiguration();
    }

    private static void LoadConfiguration()
    {
        try
        {
            if (FxpBackendConfigFiles.TryGetReadablePath(out var mainConfigPath))
            {
                var jsonContent = File.ReadAllText(mainConfigPath);
                var config = JsonConvert.DeserializeObject<MainConfig>(jsonContent);

                if (config?.FxpBackends == null)
                {
                    LogManager.Error("No FXP backend servers found in configuration");
                    return;
                }

                var loadedCount = 0;
                var disabledCount = 0;

                foreach (var server in config.FxpBackends)
                {
                    if (server.Disabled)
                    {
                        disabledCount++;
                        LogManager.Info($"FXP backend server [{server.Name ?? server.Id}] is disabled. Skipping.");
                        continue;
                    }

                    FXP_BACKEND_CONFIGS[server.Id] = new
                    {
                        Name = server.Name ?? server.Id,
                        Host = server.Host,
                        Port = server.Port,
                        Password = SecureConfig.Decrypt(server.Password),
                        Profile = server.Profile
                    };

                    LogManager.LogFxpBackend(
                        FxpBackendEventType.Connected,
                        $"Loaded config: {server.Name ?? server.Id}, Host: {server.Host}, Port: {server.Port}, Profile: {server.Profile}"
                    );

                    loadedCount++;
                }

                var suffix = disabledCount > 0 ? $" ({disabledCount} disabled)" : "";
                LogManager.Info($"Loaded {loadedCount} FXP backend configuration(s){suffix}");
            }
            else
            {
                LogManager.Error($"Main configuration file not found: {FxpBackendConfigFiles.Path}");
            }
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error loading FXP backend configurations: {ex.Message}");
        }
    }

    // One HttpClient per FXP backend endpoint, kept alive for the process lifetime.
    // Creating a client per race meant every single race paid a TCP handshake plus
    // a full TLS handshake before the command bytes could leave — pure lost race
    // time. Reusing the client keeps the connection warm (keep-alive) so a race
    // command usually goes out in a single round trip.
    private static readonly ConcurrentDictionary<string, HttpClient> FxpBackendClients =
        new ConcurrentDictionary<string, HttpClient>();

    private static readonly Dictionary<string, DateTimeOffset> LastFxpBackendHardResetByServer =
        new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, int> FxpBackendHardResetAttemptsByJob =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static readonly object FxpBackendHardResetLock = new object();

    // Long enough that a healthy-but-busy FXP backend still answers (avoiding a fallback
    // to the next server, which would submit the same spreadjob twice), short
    // enough that a dead FXP backend doesn't hold the race hostage for half a minute.
    private const int SpreadjobTimeoutSeconds = 8;

    private static string BuildFxpBackendEndpoint(string host, string port)
    {
        if (host.Contains("://"))
        {
            return host.EndsWith($":{port}", StringComparison.OrdinalIgnoreCase) ? host : $"{host}:{port}";
        }

        return $"https://{host}:{port}";
    }

    private static HttpClient GetFxpBackendClient(string endpoint, string password, string host)
    {
        var key = endpoint + "\n" + password;
        return FxpBackendClients.GetOrAdd(key, _ =>
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

    private static bool ShouldHardResetForTransferFailure(TransferResult result)
    {
        if (result == null || result.Success) return false;

        if (result.StatusCode.HasValue && result.StatusCode.Value >= 500) return true;

        var message = result.ErrorMessage ?? "";
        return message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || message.Contains("failed", StringComparison.OrdinalIgnoreCase)
               || message.Contains("not started", StringComparison.OrdinalIgnoreCase)
               || message.Contains("aborted", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReserveFxpBackendHardReset(
        string serverKey,
        string jobKey,
        TimeSpan cooldown,
        int maxAttempts,
        out TimeSpan remaining,
        out int attempt,
        out bool attemptsExhausted)
    {
        lock (FxpBackendHardResetLock)
        {
            var now = DateTimeOffset.UtcNow;
            var currentAttempts = FxpBackendHardResetAttemptsByJob.TryGetValue(jobKey, out var count) ? count : 0;

            attempt = currentAttempts + 1;
            attemptsExhausted = currentAttempts >= maxAttempts;
            if (attemptsExhausted)
            {
                remaining = TimeSpan.Zero;
                return false;
            }

            if (LastFxpBackendHardResetByServer.TryGetValue(serverKey, out var last))
            {
                var elapsed = now - last;
                if (elapsed < cooldown)
                {
                    remaining = cooldown - elapsed;
                    attemptsExhausted = false;
                    return false;
                }
            }

            LastFxpBackendHardResetByServer[serverKey] = now;
            FxpBackendHardResetAttemptsByJob[jobKey] = attempt;
            remaining = TimeSpan.Zero;
            attemptsExhausted = false;
            return true;
        }
    }

    public static async Task TryHardResetSpreadJobAsync(
        string host,
        string port,
        string password,
        string serverName,
        string releaseName,
        string reason,
        CancellationToken cancellationToken)
    {
        if (!EngineSettings.AutoHardResetFxpBackendJobs) return;
        if (string.IsNullOrWhiteSpace(host) ||
            string.IsNullOrWhiteSpace(port) ||
            string.IsNullOrWhiteSpace(password) ||
            string.IsNullOrWhiteSpace(releaseName))
        {
            return;
        }

        var endpoint = BuildFxpBackendEndpoint(host, port);
        var cooldownMinutes = Math.Max(1, EngineSettings.FxpBackendHardResetCooldownMinutes);
        var cooldown = TimeSpan.FromMinutes(cooldownMinutes);
        var maxAttempts = EngineSettings.FxpBackendHardResetMaxAttempts;
        if (maxAttempts < 1) maxAttempts = 1;
        if (maxAttempts > 5) maxAttempts = 5;

        var serverKey = string.IsNullOrWhiteSpace(serverName) ? endpoint : serverName;
        var jobKey = $"{serverKey}\u0000{releaseName}";

        if (!TryReserveFxpBackendHardReset(serverKey, jobKey, cooldown, maxAttempts, out var remaining, out var attempt, out var attemptsExhausted))
        {
            var message = attemptsExhausted
                ? $"Auto hard reset skipped; max attempts reached ({maxAttempts})."
                : $"Auto hard reset skipped; cooldown active ({Math.Ceiling(remaining.TotalMinutes)}m left).";

            LogManager.LogFxpBackend(
                FxpBackendEventType.Info,
                message,
                releaseName: releaseName,
                targetSite: serverName
            );
            return;
        }

        try
        {
            var client = GetFxpBackendClient(endpoint, password, host);
            var body = new StringContent(JsonConvert.SerializeObject(new { hard = true }), Encoding.UTF8, "application/json");
            var jobName = Uri.EscapeDataString(releaseName);
            var response = await client.PostAsync($"{endpoint}/spreadjobs/{jobName}/reset", body, cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                LogManager.LogFxpBackend(
                    FxpBackendEventType.Info,
                    $"Auto hard reset {attempt}/{maxAttempts} sent after {reason}.",
                    releaseName: releaseName,
                    targetSite: serverName
                );
            }
            else
            {
                var responseText = await response.Content.ReadAsStringAsync();
                LogManager.LogFxpBackend(
                    FxpBackendEventType.Error,
                    $"Auto hard reset failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase} {responseText}",
                    releaseName: releaseName,
                    targetSite: serverName
                );
            }
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            LogManager.LogFxpBackend(
                FxpBackendEventType.Error,
                $"Auto hard reset timed out after {SpreadjobTimeoutSeconds} seconds.",
                releaseName: releaseName,
                targetSite: serverName
            );
        }
        catch (Exception ex)
        {
            LogManager.LogFxpBackend(
                FxpBackendEventType.Error,
                $"Auto hard reset failed: {ex.Message}",
                releaseName: releaseName,
                targetSite: serverName
            );
        }
    }

    /// <summary>
    /// Starts a spreadjob transfer on FXP backend.
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
            endpoint = BuildFxpBackendEndpoint(host, port);

            // Reused, connection-warm client (see GetFxpBackendClient).
            var client = GetFxpBackendClient(endpoint, password, host);

            var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"Sending payload to {endpoint}/spreadjobs:\n{JsonConvert.SerializeObject(payload, Formatting.Indented)}");
            }

            var response = await client.PostAsync($"{endpoint}/spreadjobs", content);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                LogManager.LogFxpBackend(
                    FxpBackendEventType.SpreadJobFailed,
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

                // The FXP backend API doc does not document the POST /spreadjobs response body.
                // Treat any 2xx as started unless the response explicitly reports a
                // non-STARTED state — otherwise a working job would be logged as failed
                // and the next FXP backend server would be tried (duplicate job).
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

                    LogManager.LogFxpBackend(
                        FxpBackendEventType.SpreadJobStarted,
                        "Spreadjob started successfully",
                        spreadJobId: jobId,
                        releaseName: release,
                        targetSite: serverName
                    );

                    return TransferResult.Successful(endpoint, responseText, jobId);
                }
                else
                {
                    LogManager.LogFxpBackend(
                        FxpBackendEventType.SpreadJobFailed,
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
                LogManager.LogFxpBackend(
                    FxpBackendEventType.Error,
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
            LogManager.LogFxpBackend(
                FxpBackendEventType.Error,
                $"Request timeout ({SpreadjobTimeoutSeconds} seconds)",
                releaseName: release,
                targetSite: serverName
            );

            return TransferResult.Failed(endpoint ?? host, $"Request timeout ({SpreadjobTimeoutSeconds} seconds)");
        }
        catch (HttpRequestException ex)
        {
            LogManager.LogFxpBackend(
                FxpBackendEventType.Error,
                $"HTTP request failed: {ex.Message}",
                releaseName: release,
                targetSite: serverName
            );

            return TransferResult.FromException(endpoint ?? host, ex);
        }
        catch (Exception ex)
        {
            LogManager.LogFxpBackend(
                FxpBackendEventType.Error,
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

            if (FXP_BACKEND_CONFIGS.Count == 0)
            {
                LogManager.Error("No FXP backend servers configured.");
                return;
            }

            bool anyTransferSucceeded = false;
            bool processedReleaseLogged = false;
            var sitesByFxpBackend = GroupAllowedSitesByFxpBackend(allowedSites);

            foreach (var fxpBackendGroup in sitesByFxpBackend)
            {
                if (!FXP_BACKEND_CONFIGS.TryGetValue(fxpBackendGroup.Key, out var config))
                {
                    LogManager.LogFxpBackend(
                        FxpBackendEventType.Error,
                        $"Configured FXP backend server '{fxpBackendGroup.Key}' was not found for sites: {string.Join(", ", fxpBackendGroup.Value)}",
                        releaseName: release,
                        targetSite: fxpBackendGroup.Key
                    );
                    continue;
                }

                string serverName = config.Name;
                string host = config.Host;
                string port = config.Port;
                string password = config.Password;
                var targetSiteKeys = fxpBackendGroup.Value;
                var targetSites = targetSiteKeys
                    .Select(RemoteSiteNameForKey)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (targetSites.Count < 2)
                {
                    LogManager.LogFxpBackend(
                        FxpBackendEventType.Info,
                        $"Skipping spreadjob on FXP backend '{serverName}': only {targetSites.Count} mapped site(s) [{string.Join(", ", targetSites)}] from config(s) [{string.Join(", ", targetSiteKeys)}]. A spreadjob needs at least 2 sites on the same FXP backend server.",
                        releaseName: release,
                        targetSite: serverName
                    );
                    continue;
                }

                if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(port) || string.IsNullOrEmpty(password))
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Warning($"FXP backend '{fxpBackendGroup.Key}': Missing configuration");
                    }
                    continue;
                }

                var payload = new Dictionary<string, object>
                {
                    { "section", section },
                    { "name", release },
                    { "sites", targetSites },
                    { "profile", config.Profile }
                };

                // Add sites_dlonly array (not string)
                var sitesDlOnlyForServer = sitesDlOnly
                    .Where(site => targetSiteKeys.Contains(site, StringComparer.OrdinalIgnoreCase))
                    .Select(RemoteSiteNameForKey)
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (sitesDlOnlyForServer.Any())
                {
                    payload.Add("sites_dlonly", sitesDlOnlyForServer);

                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Debug($"sites_dlonly: [{string.Join(", ", sitesDlOnlyForServer)}]");
                    }
                }

                LogManager.LogFxpBackend(
                    FxpBackendEventType.SpreadJobSent,
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
                            targetSites,
                            cts.Token,
                            ircChannel,
                            host,
                            port,
                            password,
                            serverName
                        ));
                    }

                    if (!transferResult.JobId.HasValue)
                    {
                        LogManager.LogFxpBackend(
                            FxpBackendEventType.SpreadJobCompleted,
                            "Transfer completed successfully",
                            releaseName: release,
                            targetSite: serverName
                        );
                    }

                    // Store the PLAIN release name — IsReleaseProcessedAsync does an exact
                    // match on it, so a decorated string kills duplicate detection entirely
                    // (re-announces would be raced again). Timestamp/section/sites have
                    // their own columns.
                    if (!processedReleaseLogged)
                    {
                        SQLiteHelper.LogProcessedRelease(
                            releaseName: release,
                            category: section,
                            siteName: string.Join(",", allowedSites),
                            dateProcessed: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                            pretime: 0
                        );
                        processedReleaseLogged = true;
                    }
                }
                else
                {
                    LogManager.LogFxpBackend(
                        FxpBackendEventType.SpreadJobFailed,
                        $"Transfer failed: {transferResult.ErrorMessage}",
                        releaseName: release,
                        targetSite: serverName
                    );

                    if (ShouldHardResetForTransferFailure(transferResult))
                    {
                        await TryHardResetSpreadJobAsync(
                            host,
                            port,
                            password,
                            serverName,
                            release,
                            transferResult.ErrorMessage,
                            CancellationToken.None
                        );
                    }
                }
            }

            if (!anyTransferSucceeded)
            {
                LogManager.Error($"All FXP backend servers failed for release '{release}'");
            }
        }
        catch (Exception ex)
        {
            LogManager.Exception(ex, $"Exception in HandleTransferJob for '{release}'");
        }
    }

    private static Dictionary<string, List<string>> GroupAllowedSitesByFxpBackend(IEnumerable<string> allowedSites)
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var fallbackKey = FXP_BACKEND_CONFIGS.Keys.FirstOrDefault() ?? "";

        foreach (var site in allowedSites.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var fxpBackendKey = ResolveFxpBackendKeyForSite(site, fallbackKey);
            if (string.IsNullOrWhiteSpace(fxpBackendKey))
                continue;

            if (!groups.TryGetValue(fxpBackendKey, out var sites))
            {
                sites = new List<string>();
                groups[fxpBackendKey] = sites;
            }

            if (!sites.Contains(site, StringComparer.OrdinalIgnoreCase))
                sites.Add(site);
        }

        return groups;
    }

    private static string RemoteSiteNameForKey(string siteKey)
    {
        if (SiteConfigManager.TryGetSiteConfig(siteKey, out var siteConfig))
        {
            var remoteName = siteConfig.SiteSettings?.Sitename?.Trim();
            if (!string.IsNullOrWhiteSpace(remoteName))
                return remoteName;
        }

        return siteKey;
    }

    private static string ResolveFxpBackendKeyForSite(string site, string fallbackKey)
    {
        if (!SiteConfigManager.TryGetSiteConfig(site, out var siteConfig))
            return fallbackKey;

        var configured = siteConfig.SiteSettings?.FxpBackendId?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
            return fallbackKey;

        var keyMatch = FXP_BACKEND_CONFIGS.Keys.FirstOrDefault(k =>
            string.Equals(k, configured, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(keyMatch))
            return keyMatch;

        foreach (var kvp in FXP_BACKEND_CONFIGS)
        {
            string name = kvp.Value.Name;
            if (string.Equals(name, configured, StringComparison.OrdinalIgnoreCase))
                return kvp.Key;
        }

        LogManager.LogFxpBackend(
            FxpBackendEventType.Error,
            $"[{site}] is mapped to FXP backend server [{configured}], but that server is disabled or not loaded. Skipping this site for the race.",
            targetSite: site
        );
        return "";
    }
    
    // ============================================================
    //  TRANSFERJOBS (FXP / DOWNLOAD / UPLOAD) – used for requests
    // ============================================================

    /// <summary>
    /// Low-level HTTP helper that POSTS to /transferjobs on FXP backend.
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
                // FXP backend uses self-signed cert; this is equivalent to curl -k
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
                LogManager.LogFxpBackend(
                    FxpBackendEventType.SpreadJobFailed,
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
            LogManager.LogFxpBackend(
                FxpBackendEventType.Error,
                "Transferjob request timeout (30 seconds)",
                releaseName: releaseName,
                targetSite: serverName
            );

            return TransferResult.Failed(endpoint ?? host, "Transferjob request timeout (30 seconds)");
        }
        catch (Exception ex)
        {
            LogManager.LogFxpBackend(
                FxpBackendEventType.Error,
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
    ///   - sent as "dst_path" (can be a real path or a FXP backend section name).
    /// </summary>
    public static async Task<TransferResult> StartTransferJobFxp(
        string srcSite,
        string srcSectionOrPath,
        bool srcIsSection,
        string dstSite,
        string dstPath,
        string releaseName)
    {
        // pick the first FXP backend config (same as GetJobStats)
        var config = FXP_BACKEND_CONFIGS.Values.FirstOrDefault();
        if (config == null)
        {
            LogManager.Error("No FXP backend configuration available for transferjob");
            return TransferResult.Failed("NO_CONFIG", "No FXP backend configuration available");
        }

        return await StartTransferJobFxp(
            srcSite, srcSectionOrPath, srcIsSection,
            dstSite, dstPath, releaseName,
            (string)config.Host, (string)config.Port, (string)config.Password, (string)config.Name);
    }

    /// <summary>
    /// Same as above but posts the FXP job to an explicitly given FXP backend instance
    /// (host/port/password) instead of the first one from fxp_backend_config.json.
    /// Used by the Pre manager, which keeps its own FXP backend server list.
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
        // Build payload exactly as in FXP backend docs for an FXP job
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
            LogManager.LogFxpBackend(
                FxpBackendEventType.SpreadJobFailed,
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
        [JsonProperty(FxpBackendJsonKeys.Backends)]
        public List<FxpBackend> FxpBackends { get; set; }

        [JsonProperty(FxpBackendJsonKeys.LegacyBackends, NullValueHandling = NullValueHandling.Ignore)]
        private List<FxpBackend> LegacyBackends
        {
            get => null;
            set
            {
                if (value != null && value.Count > 0 && (FxpBackends == null || FxpBackends.Count == 0))
                    FxpBackends = value;
            }
        }

        [JsonProperty("jobs")]
        public JobSettings Jobs { get; set; }
    }

    public class FxpBackend
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

        [JsonProperty("disabled")]
        public bool Disabled { get; set; }
    }

    public class JobSettings
    {
        [JsonProperty("spreadjob")]
        public bool Spreadjob { get; set; }

        [JsonProperty("fxpjob")]
        public bool Fxpjob { get; set; }
    }
}
