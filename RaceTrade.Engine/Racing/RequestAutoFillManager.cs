using RaceTrade;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RaceTrader
{
    public class RequestEntry
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string User { get; set; }
        public string RawLine { get; set; }

        public override string ToString()
        {
            return $"{Id}: {Name} (by {User})";
        }
    }

    /// <summary>
    /// Pure logic helpers for parsing / building request stuff.
    /// </summary>
    public static class RequestAutoFillManager
    {
        /// <summary>
        /// Requests we are currently filling (key: dstSite|requestId|name). A request
        /// stays listed by SITE REQUESTS until REQFILLED is sent, so without this every
        /// poll cycle would start ANOTHER identical transferjob for the same request.
        /// </summary>
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> InFlightFills =
            new System.Collections.Concurrent.ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);

        private static string FillKey(string dstName, RequestEntry req) =>
            $"{dstName}|{req?.Id}|{req?.Name}";

        /// <summary>
        /// Parse SITE REQUESTS output using the site's template settings.
        /// NO site-specific regex is hardcoded here – everything comes from SiteSettings.
        /// </summary>
        public static List<RequestEntry> ParseRequests(string rawOutput, SiteConfig site)
        {
            var result = new List<RequestEntry>();

            if (site?.SiteSettings == null)
                return result;

            var s = site.SiteSettings;

            if (!s.RequestAutoFillEnabled)
                return result;

            if (string.IsNullOrWhiteSpace(s.RequestLinePattern))
            {
                LogManager.Warning(
                    $"[RequestAutoFill] Site '{s.Sitename}' has auto-fill enabled but no RequestLinePattern set.");
                return result;
            }

            Regex lineRegex;
            try
            {
                lineRegex = new Regex(
                    s.RequestLinePattern,
                    RegexOptions.Compiled | RegexOptions.Multiline);
            }
            catch (Exception ex)
            {
                LogManager.Error(
                    $"[RequestAutoFill] Invalid RequestLinePattern for '{s.Sitename}': {ex.Message}");
                return result;
            }

            var lines = (rawOutput ?? string.Empty)
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                var m = lineRegex.Match(line);
                if (!m.Success)
                    continue;

                // Skip requests already marked complete/filled (RequestCompletePattern),
                // otherwise they get re-fill attempts on every poll.
                if (IsLineMarkedComplete(site, line))
                    continue;

                string id = null;
                string name = null;
                string user = null;

                // Prefer named groups if they exist
                if (m.Groups["id"] != null && m.Groups["id"].Success)
                    id = m.Groups["id"].Value.Trim();

                if (m.Groups["name"] != null && m.Groups["name"].Success)
                    name = m.Groups["name"].Value.Trim();

                if (m.Groups["user"] != null && m.Groups["user"].Success)
                    user = m.Groups["user"].Value.Trim();

                // Fallbacks for badly configured patterns
                if (string.IsNullOrEmpty(id) && m.Groups.Count > 1)
                    id = m.Groups[1].Value.Trim();

                if (string.IsNullOrEmpty(name) && m.Groups.Count > 2)
                    name = m.Groups[2].Value.Trim();

                if (string.IsNullOrEmpty(name))
                    continue; // without a name it's useless

                result.Add(new RequestEntry
                {
                    Id = id,
                    Name = name,
                    User = user,
                    RawLine = line
                });
            }

            LogManager.Debug(
                $"[RequestAutoFill] Parsed {result.Count} request(s) for site '{site.SiteSettings.Sitename}'");

            return result;
        }

        /// <summary>
        /// Build the "REQFILLED" (or equivalent) command using the site's template.
        /// Example template: "SITE REQFILLED {id}" or "SITE REQFILLED {name}".
        /// </summary>
        public static string BuildFillCommand(SiteConfig site, RequestEntry request)
        {
            var s = site?.SiteSettings;
            if (s == null)
                return null;

            if (string.IsNullOrWhiteSpace(s.RequestFillTemplate))
                return null;

            return ApplyTemplate(
                s.RequestFillTemplate,
                request,
                releaseName: null,
                siteName: s.Sitename);
        }

        /// <summary>
        /// Build the destination path for the transfer job, e.g. "/REQUESTS/{name}".
        /// Always used with a transferjob – never a spreadjob.
        /// </summary>
        public static string BuildDstPath(SiteConfig site, RequestEntry request, string releaseName)
        {
            var s = site?.SiteSettings;
            if (s == null)
                return null;

            if (string.IsNullOrWhiteSpace(s.RequestDstPathTemplate))
                return null;

            return ApplyTemplate(
                s.RequestDstPathTemplate,
                request,
                releaseName,
                s.Sitename);
        }

        /// <summary>
        /// Optional helper: check if a line from SITE REQUESTS output looks "complete"
        /// according to RequestCompletePattern (if set).
        /// </summary>
        public static bool IsLineMarkedComplete(SiteConfig site, string rawLine)
        {
            var s = site?.SiteSettings;
            if (s == null)
                return false;

            if (string.IsNullOrWhiteSpace(s.RequestCompletePattern))
                return false;

            try
            {
                var r = new Regex(s.RequestCompletePattern, RegexOptions.Compiled);
                return r.IsMatch(rawLine ?? string.Empty);
            }
            catch (Exception ex)
            {
                LogManager.Error(
                    $"[RequestAutoFill] Invalid RequestCompletePattern for '{s.Sitename}': {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// One poll step: does "/raw" with the request list command, parses and logs the results,
        /// and returns the list of open RequestEntry objects.
        /// This is what the background runner calls every X seconds per site.
        /// </summary>
        public static async Task<List<RequestEntry>> PollOnceAndLogAsync(
            SiteConfig site,
            CancellationToken token)
        {
            var empty = new List<RequestEntry>();

            if (site?.SiteSettings == null)
                return empty;

            var s = site.SiteSettings;

            if (!s.RequestAutoFillEnabled)
                return empty;

            if (string.IsNullOrWhiteSpace(s.RequestListCommand))
            {
                LogManager.Warning(
                    $"[RequestAutoFill] Site '{s.Sitename}' has auto-fill enabled but no RequestListCommand set.");
                return empty;
            }

            LogManager.LogCBFTP(
                CBFTPEventType.Info,
                $"[RequestAutoFill] Polling requests on '{s.Sitename}'",  // with command: {s.RequestListCommand}
                releaseName: null,
                targetSite: s.Sitename);

            // Call cbftp /raw and get the raw result for this site
            var raw = await CbftpRequestHelper.RunRawAsync(
                s.RequestListCommand,
                s.Sitename,
                token);

            if (string.IsNullOrWhiteSpace(raw))
            {
                LogManager.LogCBFTP(
                    CBFTPEventType.Info,
                    $"[RequestAutoFill] No request output received for '{s.Sitename}'.",
                    releaseName: null,
                    targetSite: s.Sitename);
                return empty;
            }
            if (EngineSettings.DebugEnabled) { 
                // Log raw SITE REQUESTS output into CBFTP log so you can see EXACTLY what the site returned
                LogManager.LogCBFTP(
                    CBFTPEventType.Info,
                    $"[RequestAutoFill] SITE REQUESTS raw output for '{s.Sitename}':\n{raw}",
                    releaseName: null,
                    targetSite: s.Sitename);
            }
            var entries = ParseRequests(raw, site);

            if (entries.Count == 0)
            {
                LogManager.LogCBFTP(
                    CBFTPEventType.Info,
                    $"[RequestAutoFill] No open requests found on '{s.Sitename}'.",
                    releaseName: null,
                    targetSite: s.Sitename);
                return empty;
            }

            // Log a compact summary as well
            var summary = string.Join(Environment.NewLine,
                entries.Select(e => $"  {e.Id ?? "?"}: {e.Name} (by {e.User})"));

            LogManager.LogCBFTP(
                CBFTPEventType.Info,
                $"[RequestAutoFill] Found {entries.Count} open request(s) on '{s.Sitename}':\n{summary}",
                releaseName: null,
                targetSite: s.Sitename);

            return entries;
        }

        /// <summary>
        /// Replace placeholders in template:
        /// {id}, {name}, {user}, {release}, {sitename}
        /// </summary>
        private static string ApplyTemplate(
            string template,
            RequestEntry request,
            string releaseName,
            string siteName)
        {
            if (string.IsNullOrWhiteSpace(template))
                return null;

            var result = template;

            if (request != null)
            {
                result = result.Replace("{id}", request.Id ?? string.Empty);
                result = result.Replace("{name}", request.Name ?? string.Empty);
                result = result.Replace("{user}", request.User ?? string.Empty);
            }

            result = result.Replace("{release}", releaseName ?? string.Empty);
            result = result.Replace("{sitename}", siteName ?? string.Empty);

            return result.Trim();
        }

        /// <summary>
        /// Very simple parser for SITE SEARCH output: finds a line containing
        /// the release name and returns the PARENT folder for src_path.
        /// Example line:
        ///   200- /ARCHIVE/TV-HD-X264/Im.a.Celebrity....-CODSWALLOP
        /// Returns:
        ///   "/ARCHIVE/TV-HD-X264"
        /// </summary>
        /// <summary>
        /// Parse SITE SEARCH output and return the PARENT folder for src_path.
        /// Uses the known releaseName as an anchor, so it works even if stats change.
        /// Example line:
        ///   200- /ARCHiVE/X264-HD-1080P/Lunana.A.Yak.In.The.Classroom.2019.1080p.BluRay.x264-TABULARiA (69F/6286.0M/544d 12h)
        /// Returns:
        ///   "/ARCHiVE/X264-HD-1080P"
        /// </summary>
        private static string ExtractReleasePathFromSearch(string rawSearch, string releaseName)
        {
            if (string.IsNullOrWhiteSpace(rawSearch) || string.IsNullOrWhiteSpace(releaseName))
                return null;

            var lines = rawSearch
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            var nameLower = releaseName.ToLowerInvariant();

            foreach (var line in lines)
            {
                // only lines that actually contain the full release name
                if (!line.ToLowerInvariant().Contains(nameLower))
                    continue;

                // skip "200- " prefix etc, keep from first '/'
                var idxSlash = line.IndexOf('/');
                if (idxSlash < 0)
                    continue;

                var pathPart = line.Substring(idxSlash).Trim();
                // e.g. "/ARCHiVE/X264-HD-1080P/Lunana...TABULARiA (69F/6286.0M/544d 12h)"

                // find the release name inside that path part
                var nameIdx = pathPart.IndexOf(releaseName, StringComparison.OrdinalIgnoreCase);
                if (nameIdx < 0)
                    continue;

                // find the slash just BEFORE the release name
                var slashBeforeName = pathPart.LastIndexOf('/', nameIdx);
                if (slashBeforeName <= 0)
                    continue;

                // everything before that slash is the parent folder
                var parentPath = pathPart.Substring(0, slashBeforeName);

                LogManager.Debug(
                    $"[RequestAutoFill] Extracted parent path '{parentPath}' for release '{releaseName}' from line: {line}");

                return parentPath;
            }

            // no usable line found
            return null;
        }

        /// <summary>
        /// Which sites may act as SOURCE when filling requests for another site.
        /// </summary>
        public static IEnumerable<SiteConfig> GetFillSourceSites(
            SiteConfig requestSite,
            IEnumerable<SiteConfig> allSites,
            bool allowSelfFill)
        {
            if (requestSite?.SiteSettings == null || allSites == null)
                return Enumerable.Empty<SiteConfig>();

            var reqName = requestSite.SiteSettings.Sitename;

            var query = allSites.Where(s =>
                s?.SiteSettings != null &&
                s.SiteSettings.RequestCanFillSource &&       // only sites marked "Can be used for filling requests"
                !s.SiteSettings.DisableSite);                // must NOT be disabled

            // Optionally exclude the request site itself:
            if (!allowSelfFill)
            {
                query = query.Where(s =>
                    !string.Equals(
                        s.SiteSettings.Sitename,
                        reqName,
                        StringComparison.OrdinalIgnoreCase));
            }

            return query.ToList();
        }



        /// <summary>
        /// Normalize a release name for cbftp SITE SEARCH:
        /// - convert various Unicode dash characters to ASCII '-'
        /// </summary>
        private static string NormalizeReleaseNameForSearch(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            // list of dash-like characters to normalize
            char[] dashLikes = new[]
            {
        '\u2010', // Hyphen
        '\u2011', // Non-breaking hyphen
        '\u2012', // Figure dash
        '\u2013', // En dash
        '\u2014', // Em dash
        '\u2212'  // Minus sign
    };

            var chars = name.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (dashLikes.Contains(chars[i]))
                    chars[i] = '-';
            }

            return new string(chars);
        }


        private static async Task WaitForTransferAndReqfillAsync(
    string jobName,
    SiteConfig requestSite,
    RequestEntry req,
    string dstName,
    CancellationToken token)
        {
            try
            {
                int emptyCount = 0;

                while (!token.IsCancellationRequested)
                {
                    var stats = await CbftpRacer.GetTransferJobStats(jobName);

                    if (stats == null)
                    {
                        // not found – maybe still initializing or already purged
                        emptyCount++;
                        if (emptyCount > 6) // e.g. ~60 seconds with 10s interval
                        {
                            LogManager.LogCBFTP(
                                CBFTPEventType.Info,
                                $"[RequestAutoFill] Transferjob '{jobName}' not found while waiting to REQFILL.",
                                releaseName: jobName,
                                targetSite: dstName);
                            return;
                        }
                    }
                    else
                    {
                        emptyCount = 0;
                        var status = (stats.Status ?? "").ToUpperInvariant();

                        if (status == "DONE")
                        {
                            LogManager.LogCBFTP(
                                CBFTPEventType.Info,
                                $"[RequestAutoFill] Transferjob for '{jobName}' is DONE ({stats.FilesTransferred}/{stats.FilesTotal} files, {stats.AverageSpeed:F1} MB/s). Sending REQFILLED...",
                                releaseName: jobName,
                                targetSite: dstName);

                            var fillCmd = BuildFillCommand(requestSite, req);
                            if (!string.IsNullOrWhiteSpace(fillCmd))
                            {
                                await CbftpRequestHelper.RunRawAsync(fillCmd, dstName, token);
                            }

                            LogManager.LogCBFTP(
                                CBFTPEventType.SpreadJobCompleted,
                                $"[RequestAutoFill] Request '{jobName}' on '{dstName}' filled (after successful transferjob).",
                                releaseName: jobName,
                                targetSite: dstName);
                            return;
                        }

                        if (status == "FAILED" || status == "TIMEOUT" || status == "ABORTED")
                        {
                            LogManager.LogCBFTP(
                                CBFTPEventType.SpreadJobFailed,
                                $"[RequestAutoFill] Transferjob for '{jobName}' ended with status {status}, NOT sending REQFILLED.",
                                releaseName: jobName,
                                targetSite: dstName);
                            return;
                        }
                    }

                    await Task.Delay(TimeSpan.FromSeconds(10), token);
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown – ignore
            }
            catch (Exception ex)
            {
                LogManager.Error($"[RequestAutoFill] Error while waiting for transferjob '{jobName}' to finish: {ex.Message}");
            }
            finally
            {
                // Allow this request to be picked up again (retried or re-verified)
                // on a future poll now that the waiter is done.
                InFlightFills.TryRemove(FillKey(dstName, req), out _);
            }
        }

        /// <summary>
        /// For one request site:
        ///   - look at all RequestEntry objects
        ///   - for each, search on all "source" sites (RequestCanFillSource == true)
        ///   - if found on a source site:
        ///       * start FXP src -> dst (request site) using transferjobs
        ///       * send REQFILLED (or whatever RequestFillTemplate is)
        /// </summary>
        public static async Task TryFillRequestsForSiteAsync(
            SiteConfig requestSite,
            IEnumerable<RequestEntry> requests,
            IEnumerable<SiteConfig> allSites,
            CancellationToken token)
        {
            if (requestSite?.SiteSettings == null || requests == null || allSites == null)
                return;

            var dstSettings = requestSite.SiteSettings;
            var dstName = dstSettings.Sitename;

            var sourceSites = GetFillSourceSites(
                requestSite,
                allSites,
                allowSelfFill: false).ToList();
            // or, if you prefer explicit:
            //// var sourceSites = RequestAutoFillManager.GetFillSourceSites(
            ////     requestSite, allSites, allowSelfFill: false).ToList();

            if (sourceSites.Count == 0)
            {
                LogManager.Debug($"[RequestAutoFill] No source sites configured for '{dstName}'.");
                return;
            }

            foreach (var req in requests)
            {
                var originalName = req.Name;
                if (string.IsNullOrWhiteSpace(originalName))
                    continue;

                // Skip requests that already have a fill in flight — SITE REQUESTS keeps
                // listing them until REQFILLED, so every poll would otherwise start
                // another identical transferjob.
                if (InFlightFills.ContainsKey(FillKey(dstName, req)))
                {
                    LogManager.Debug($"[RequestAutoFill] Fill already in progress for '{originalName}' on '{dstName}', skipping.");
                    continue;
                }

                // normalize ONLY for searching; keep original for dst path / logging if you want
                var searchName = NormalizeReleaseNameForSearch(originalName);

                LogManager.LogCBFTP(
                    CBFTPEventType.Info,
                    $"[RequestAutoFill] Trying to auto-fill request '{originalName}' on '{dstName}' (searching as '{searchName}')...",
                    releaseName: originalName,
                    targetSite: dstName);

                foreach (var src in sourceSites)
                {
                    var srcName = src.SiteSettings.Sitename;

                    LogManager.LogCBFTP(
                        CBFTPEventType.Info,
                        $"[RequestAutoFill] Searching '{srcName}' for '{searchName}'...",
                        releaseName: originalName,
                        targetSite: srcName);

                    var searchCmd = $"SITE SEARCH {searchName}";
                    var rawSearch = await CbftpRequestHelper.RunRawAsync(searchCmd, srcName, token);

                    if (string.IsNullOrWhiteSpace(rawSearch))
                    {
                        LogManager.LogCBFTP(
                            CBFTPEventType.Info,
                            $"[RequestAutoFill] No SITE SEARCH result for '{searchName}' on '{srcName}'.",
                            releaseName: originalName,
                            targetSite: srcName);
                        continue;
                    }

                    LogManager.LogCBFTP(
                        CBFTPEventType.Info,
                        $"[RequestAutoFill] SITE SEARCH raw output from '{srcName}' for '{searchName}':\n{rawSearch}",
                        releaseName: originalName,
                        targetSite: srcName);

                    var srcPath = ExtractReleasePathFromSearch(rawSearch, searchName);
                    if (string.IsNullOrEmpty(srcPath))
                    {
                        LogManager.LogCBFTP(
                            CBFTPEventType.Info,
                            $"[RequestAutoFill] Could not extract path for '{searchName}' on '{srcName}' from SITE SEARCH output.",
                            releaseName: originalName,
                            targetSite: srcName);
                        continue;
                    }

                    LogManager.LogCBFTP(
                        CBFTPEventType.Info,
                        $"[RequestAutoFill] Found '{searchName}' on '{srcName}' at path '{srcPath}'.",
                        releaseName: originalName,
                        targetSite: srcName);

                    var dstPath = RequestAutoFillManager.BuildDstPath(requestSite, req, originalName);
                    if (string.IsNullOrWhiteSpace(dstPath))
                    {
                        LogManager.Warning(
                            $"[RequestAutoFill] '{dstName}' has no RequestDstPathTemplate, cannot fill '{originalName}'.");
                        break; // config broken for this dst
                    }

                    var ok = await CbftpRacer.StartRequestTransferJob(
                        srcName,
                        dstName,
                        dstPath,
                        originalName,
                        srcPath,
                        srcIsSection: false);

                    if (!ok)
                    {
                        LogManager.Warning(
                            $"[RequestAutoFill] Transferjob failed to START for '{originalName}' from '{srcName}' -> '{dstName}'. Trying next source...");
                        continue;
                    }

                    // Mark this request as in flight until the waiter finishes.
                    InFlightFills.TryAdd(FillKey(dstName, req), 0);

                    // Do NOT REQFILL here.
                    // Start a background waiter that will REQFILL when cbftp reports status DONE.
                    LogManager.LogCBFTP(
                        CBFTPEventType.Info,
                        $"[RequestAutoFill] Transferjob started for '{originalName}' from '{srcName}' -> '{dstName}' (dst: '{dstPath}'). Waiting for completion before REQFILL...",
                        releaseName: originalName,
                        targetSite: dstName);

                    _ = Task.Run(() => WaitForTransferAndReqfillAsync(
                            originalName,
                            requestSite,
                            req,
                            dstName,
                            token));

                    // done with this request – we let the waiter handle REQFILL
                    break;
                }
            }

        }
    }

    /// <summary>
    /// Background polling loop for all sites with RequestAutoFillEnabled.
    /// You start/stop this from MainApp (e.g. when trader starts/stops).
    /// </summary>
    public static class RequestAutoFillRunner
    {
        private static readonly object _sync = new object();
        private static CancellationTokenSource _cts;
        private static Task _loopTask;
        private static List<SiteConfig> _sites = new List<SiteConfig>();
        private static List<SiteConfig> _allSites = new List<SiteConfig>();

        /// <summary>
        /// Start polling for all given sites that have RequestAutoFillEnabled.
        /// </summary>
        public static void StartForAllSites(IEnumerable<SiteConfig> sites)
        {
            if (sites == null)
                return;

            lock (_sync)
            {
                StopInternal_NoLock();

                // all known sites (for source search)
                _allSites = sites
                    .Where(s => s != null)
                    .ToList();

                // only these will be polled for requests
                _sites = _allSites
                    .Where(s => s?.SiteSettings?.RequestAutoFillEnabled == true)
                    .ToList();

                if (_sites.Count == 0)
                {
                    LogManager.Info("[RequestAutoFill] No sites with auto-fill enabled; polling not started.");
                    return;
                }

                _cts = new CancellationTokenSource();
                _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), _cts.Token);

                LogManager.Info($"[RequestAutoFill] Started polling for {_sites.Count} site(s).");
            }
        }

        /// <summary>
        /// Stop polling loop.
        /// </summary>
        public static void Stop()
        {
            lock (_sync)
            {
                StopInternal_NoLock();
            }
        }

        private static void StopInternal_NoLock()
        {
            if (_cts != null)
            {
                try { _cts.Cancel(); } catch { /* ignore */ }
                _cts = null;
            }

            _loopTask = null;
            _sites.Clear();
            _allSites.Clear();
        }

        private static async Task RunLoopAsync(CancellationToken token)
        {
            // "next poll time" per site
            var nextPollTimes = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

            while (!token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;

                // Snapshot under the lock - Stop()/Start() clear _sites/_allSites from
                // another thread, which would throw "Collection was modified" and kill
                // this loop (an unobserved exception on a fire-and-forget task).
                List<SiteConfig> sitesSnapshot;
                List<SiteConfig> allSitesSnapshot;
                lock (_sync)
                {
                    sitesSnapshot = _sites.ToList();
                    allSitesSnapshot = _allSites.ToList();
                }

                foreach (var site in sitesSnapshot)
                {
                    if (token.IsCancellationRequested)
                        break;

                    var s = site.SiteSettings;
                    if (!s.RequestAutoFillEnabled)
                        continue;

                    var pollSeconds = s.RequestPollSeconds <= 0
                        ? 300
                        : s.RequestPollSeconds;

                    var key = s.Sitename ?? Guid.NewGuid().ToString();

                    if (!nextPollTimes.TryGetValue(key, out var next))
                    {
                        next = DateTime.MinValue;
                    }

                    if (now >= next)
                    {
                        // schedule next poll
                        nextPollTimes[key] = now.AddSeconds(pollSeconds);

                        try
                        {
                            // 1) get open requests
                            var entries = await RequestAutoFillManager.PollOnceAndLogAsync(site, token);

                            // 2) try to auto-fill them from other sites
                            if (entries != null && entries.Count > 0 && allSitesSnapshot.Count > 0)
                            {
                                await RequestAutoFillManager.TryFillRequestsForSiteAsync(
                                    site,
                                    entries,
                                    allSitesSnapshot,
                                    token);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // normal on shutdown
                        }
                        catch (Exception ex)
                        {
                            LogManager.Error($"[RequestAutoFill] Poll error for '{s.Sitename}': {ex.Message}");
                        }
                    }
                }

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }

            LogManager.Info("[RequestAutoFill] Polling loop stopped.");
        }
    }

    /// <summary>
    /// Small helper that calls cbftp /raw using your existing cbftp_config.json.
    /// This is used ONLY for SITE REQUESTS, SITE SEARCH, and REQFILLED commands.
    /// </summary>
    internal static class CbftpRequestHelper
    {
        public static async Task<string> RunRawAsync(
            string command,
            string siteName,
            CancellationToken token)
        {
            string endpoint = null;

            try
            {
                string configPath = Path.Combine("cbftp", "cbftp_config.json");
                if (!File.Exists(configPath))
                {
                    LogManager.Error("[RequestAutoFill] cbftp_config.json not found, cannot call /raw.");
                    return null;
                }

                // Reuse your existing Config / CbftpServer classes from RaceTrade
                var jsonContent = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<Config>(jsonContent);

                var server = config?.CbftpServers?.FirstOrDefault();
                if (server == null)
                {
                    LogManager.Error("[RequestAutoFill] No cbftp servers found in cbftp_config.json.");
                    return null;
                }

                var password = SecureConfig.Decrypt(server.Password);

                bool allowInsecureSsl = EngineSettings.AllowInsecureSsl;

                using var handler = new HttpClientHandler
                {
                    // self-signed TLS (curl -k equivalent)
                    ServerCertificateCustomValidationCallback = (sender, cert, chain, sslPolicyErrors) =>
                    {
                        if (sslPolicyErrors == System.Net.Security.SslPolicyErrors.None)
                            return true;

                        return allowInsecureSsl;
                    }
                };

                using var client = new HttpClient(handler)
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                // USE DECRYPTED PASSWORD HERE
                var byteArray = Encoding.ASCII.GetBytes(":" + password);
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(byteArray));
                client.DefaultRequestHeaders.Accept.Add(
                    new MediaTypeWithQualityHeaderValue("application/json"));

                if (server.Host.Contains("://"))
                    endpoint = server.Host.EndsWith($":{server.Port}") ? server.Host : $"{server.Host}:{server.Port}";
                else
                    endpoint = $"https://{server.Host}:{server.Port}";

                var payload = new
                {
                    command = command,
                    sites = new[] { siteName }
                };

                var json = JsonConvert.SerializeObject(payload, Formatting.None);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                if (EngineSettings.DebugEnabled)
                {
                    LogManager.Debug($"[RequestAutoFill] POST {endpoint}/raw payload:\n" +
                                     JsonConvert.SerializeObject(payload, Formatting.Indented));
                }

                var response = await client.PostAsync($"{endpoint}/raw", content, token);
                var responseText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    LogManager.LogCBFTP(
                        CBFTPEventType.Error,
                        $"[RequestAutoFill] /raw HTTP {(int)response.StatusCode}: {response.ReasonPhrase}",
                        releaseName: null,
                        targetSite: siteName
                    );

                    return null;
                }

                if (EngineSettings.DebugEnabled)
                {
                    LogManager.Debug($"[RequestAutoFill] /raw response for {siteName}:\n{responseText}");
                }

                try
                {
                    var root = JObject.Parse(responseText);
                    var successes = root["successes"] as JArray;

                    if (successes != null && successes.Count > 0)
                    {
                        // Prefer matching site name
                        var match = successes
                            .FirstOrDefault(x =>
                                string.Equals((string)x["name"], siteName, StringComparison.OrdinalIgnoreCase));

                        if (match != null)
                            return (string)match["result"];

                        // Fallback: first success
                        return (string)successes[0]["result"];
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Error($"[RequestAutoFill] Failed to parse /raw JSON: {ex.Message}");
                    // Fallback to raw text
                }

                return responseText;
            }
            catch (TaskCanceledException)
            {
                LogManager.LogCBFTP(
                    CBFTPEventType.Error,
                    "[RequestAutoFill] /raw request timeout (30 seconds)",
                    releaseName: null,
                    targetSite: siteName
                );
                return null;
            }
            catch (Exception ex)
            {
                LogManager.LogCBFTP(
                    CBFTPEventType.Error,
                    $"[RequestAutoFill] /raw HTTP error: {ex.Message}",
                    releaseName: null,
                    targetSite: siteName
                );

                return null;
            }
        }
    }
}
