using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RaceTrade;
using System.Collections.Concurrent;
using RaceTrader;

/// <summary>
/// Helper class for racing operations with caching and performance optimizations.
/// COMPLETELY REFACTORED with all critical fixes.
/// </summary>
public static class RaceHelper
{
    // Thread-safe caches
    private static readonly ConcurrentDictionary<string, Dictionary<string, (List<string> GeneralRules, List<MappedTag> MappedTags)>> SiteBasedCache = new();
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> CbftpToIrcSectionCache = new();
    private static readonly ConcurrentDictionary<string, bool> InProgressReleases = new();
    private static readonly List<JObject> allSiteConfigs = new List<JObject>();
    private static readonly object configLock = new();
    private static readonly string[] ReservedSiteConfigNames = { "new_site", "template", "example" };

    /// <summary>
    /// Guard against catastrophic backtracking in user/config supplied patterns.
    /// Without this a pathological trigger/blacklist regex can hang the filter thread.
    /// </summary>
    private static readonly TimeSpan RegexSafeTimeout = TimeSpan.FromMilliseconds(250);
    // NOTE: RulesEngine holds per-evaluation state (_sectionRules/_tagRules), so it is
    // NOT shared statically anymore — each racing path creates its own instance and
    // loads rules immediately before evaluating (avoids stale rules + concurrency races).

    // Global blacklist (set from MainApp)
    private static List<string> globalBlacklist = new List<string>();
    private static readonly object blacklistLock = new();

    /// <summary>
    /// Sets the global blacklist patterns from MainApp.
    /// </summary>
    public static void SetGlobalBlacklist(List<string> patterns)
    {
        lock (blacklistLock)
        {
            // Copy — the caller (MainApp) keeps mutating its own list on the UI thread,
            // and sharing the reference would defeat blacklistLock entirely.
            globalBlacklist = patterns != null ? new List<string>(patterns) : new List<string>();
        }
    }

    /// <summary>
    /// Checks if a release matches any global blacklist pattern.
    /// </summary>
    private static bool IsGloballyBlacklisted(string releaseName, out string matchedPattern)
    {
        matchedPattern = null;

        lock (blacklistLock)
        {
            if (!globalBlacklist.Any())
                return false;

            foreach (var pattern in globalBlacklist)
            {
                try
                {
                    string regexPattern;

                    if (pattern.Contains("*") || pattern.Contains("?"))
                    {
                        regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
                    }
                    else
                    {
                        regexPattern = pattern;
                    }

                    if (Regex.IsMatch(releaseName, regexPattern, RegexOptions.IgnoreCase, RegexSafeTimeout))
                    {
                        matchedPattern = pattern;
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Error($"Invalid global blacklist pattern '{pattern}': {ex.Message}");
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Wildcard blacklist matching shared by the global and per-site blacklists:
    /// * and ? act as wildcards; anything else is matched as a literal substring.
    /// </summary>
    private static bool MatchesBlacklistPattern(string releaseName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        try
        {
            string regexPattern;

            if (pattern.Contains("*") || pattern.Contains("?"))
            {
                regexPattern = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            }
            else
            {
                regexPattern = Regex.Escape(pattern);
            }

            return Regex.IsMatch(releaseName, regexPattern, RegexOptions.IgnoreCase, RegexSafeTimeout);
        }
        catch (Exception ex)
        {
            LogManager.Error($"Invalid blacklist pattern '{pattern}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Section release skiplist matching. This is intentionally about the announce
    /// release name, not CBFTP file or directory names.
    /// </summary>
    private static bool MatchesReleaseSkiplist(string releaseName, IEnumerable<string> patterns, out string matchedPattern)
    {
        matchedPattern = null;

        if (string.IsNullOrWhiteSpace(releaseName) || patterns == null)
            return false;

        foreach (var pattern in patterns)
        {
            if (MatchesReleaseNamePattern(releaseName, pattern))
            {
                matchedPattern = pattern;
                return true;
            }
        }

        return false;
    }

    private static bool MatchesReleaseNamePattern(string releaseName, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
            return false;

        pattern = pattern.Trim();

        if (pattern.Contains("*") || pattern.Contains("?"))
        {
            return WildcardMatch(releaseName, pattern);
        }

        return releaseName.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool WildcardMatch(string value, string pattern)
    {
        int valueIndex = 0;
        int patternIndex = 0;
        int starIndex = -1;
        int retryIndex = 0;

        while (valueIndex < value.Length)
        {
            if (patternIndex < pattern.Length &&
                (pattern[patternIndex] == '?' ||
                 char.ToUpperInvariant(pattern[patternIndex]) == char.ToUpperInvariant(value[valueIndex])))
            {
                patternIndex++;
                valueIndex++;
            }
            else if (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            {
                starIndex = patternIndex++;
                retryIndex = valueIndex;
            }
            else if (starIndex != -1)
            {
                patternIndex = starIndex + 1;
                valueIndex = ++retryIndex;
            }
            else
            {
                return false;
            }
        }

        while (patternIndex < pattern.Length && pattern[patternIndex] == '*')
            patternIndex++;

        return patternIndex == pattern.Length;
    }

    private static bool IsConfiguredGlobalPreBotMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return false;

        var match = Regex.Match(mode, @"^Global\s+PreBot\s*\(([^)]+)\)\s*$", RegexOptions.IgnoreCase);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value);
    }

    /// <summary>
    /// Loads all site configurations from disk.
    /// Now properly clears caches before reloading.
    /// </summary>
    public static void LoadAllSiteConfigs()
    {
        lock (configLock)
        {
            // Clear all caches
            allSiteConfigs.Clear();
            SiteBasedCache.Clear();
            CbftpToIrcSectionCache.Clear();

            var directory = "sites";

            if (!Directory.Exists(directory))
            {
                Console.WriteLine($"Directory '{directory}' does not exist.");
                return;
            }

            foreach (var filePath in Directory.GetFiles(directory, "*.json"))
            {
                var fileName = Path.GetFileName(filePath);

                // Skip old placeholder/template files if a user still has them in sites\.
                var siteConfigName = Path.GetFileNameWithoutExtension(filePath);
                if (ReservedSiteConfigNames.Contains(siteConfigName, StringComparer.OrdinalIgnoreCase))
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Debug($"Skipping default site configuration: {fileName}");
                    }
                    continue;
                }

                try
                {
                    var json = File.ReadAllText(filePath);
                    var siteConfig = JObject.Parse(json);

                    string siteName = siteConfig["site_settings"]?["sitename"]?.ToString() ?? "Unknown Site";
                    bool disableSite = siteConfig["site_settings"]?["disable_site"]?.ToObject<bool>() ?? true;

                    if (disableSite)
                    {
                        LogManager.Warning($"Site [{siteName}] is disabled. Skipping.");
                        continue;
                    }

                    allSiteConfigs.Add(siteConfig);

                    // Build section cache for this site
                    BuildSectionCache(siteConfig, siteName);

                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Debug($"Loaded site configuration: [{siteName}]");
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Error($"Failed to load site config '{fileName}': {ex.Message}");
                }
            }

            LogManager.Success($"Successfully loaded {allSiteConfigs.Count} site configuration(s).");
        }
    }

    /// <summary>
    /// Builds a fast O(1) lookup cache for CBFTP -> IRC section mappings.
    /// PERFORMANCE FIX: Eliminates O(n²) lookups.
    /// </summary>
    private static void BuildSectionCache(JObject siteConfig, string siteName)
    {
        var cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var sections = siteConfig["sections"] as JArray;
        if (sections != null)
        {
            foreach (var section in sections)
            {
                var ircName = section["irc_name"]?.ToString();
                if (string.IsNullOrEmpty(ircName)) continue;

                var tags = section["tags"] as JArray;
                if (tags != null)
                {
                    foreach (var tag in tags)
                    {
                        var cbftpSection = tag["map_cbftp_section"]?.ToString();
                        if (!string.IsNullOrEmpty(cbftpSection))
                        {
                            // Map CBFTP section -> IRC section
                            cache[cbftpSection] = ircName;
                        }
                    }
                }
            }
        }

        CbftpToIrcSectionCache[siteName] = cache;

        if (EngineSettings.DebugEnabled)
        {
            LogManager.Debug($"Built section cache for '{siteName}': {cache.Count} mapping(s)");
        }
    }


    /// <summary>
    /// Compact one-line summary of the attributes the rule engine saw. The engine only
    /// returns ALLOW/DROP, so this is the next best thing: it shows what it judged.
    /// </summary>
    private static string DescribeInput(Dictionary<string, string> input)
    {
        if (input == null) return "";

        var interesting = new[] { "group", "resolution", "source", "codec", "range", "year", "repeat" };

        return string.Join(", ", interesting
            .Where(k => input.ContainsKey(k) && !string.IsNullOrEmpty(input[k]))
            .Select(k => $"{k}={input[k]}"));
    }

    /// <summary>
    /// Filters allowed sites for a release.
    /// Returns a FilterResult with a proper status code, checks the database for
    /// duplicates, uses cached lookups, and for Global PreBots checks the original IRC
    /// section instead of the reverse mapping.
    ///
    /// Every rejection is also recorded in <see cref="FilterResult.Skips"/> and pushed to
    /// <see cref="RaceDiagnostics"/>, which is what the Skips page and Test Release read.
    /// </summary>
    public static async Task<FilterResult> FilterAllowedSites(
        Dictionary<string, string> raceSections,
        Dictionary<string, string> mappings,
        List<string> blacklist,
        string cbftpSection,
        string releaseName,
        string message,
        string sectionPrefix,
        string sectionSuffix,
        string currentSiteName,
        string originalIrcSection = null)
    {
        // Declared before the early returns so those results carry their reason too —
        // otherwise Test Release showed a bare status for a globally blacklisted release.
        var skips = new List<SkipRecord>();

        SkipRecord Skip(string site, SkipReason reason, string detail, string section)
        {
            var record = RaceDiagnostics.Report(
                releaseName, reason, detail, site, section ?? cbftpSection, currentSiteName);
            skips.Add(record);
            return record;
        }

        FilterResult WithSkips(FilterResult result)
        {
            result.Skips.AddRange(skips);
            return result;
        }

        if (IsGloballyBlacklisted(releaseName, out string matchedPattern))
        {
            LogManager.Warning($"Release '{releaseName}' globally blacklisted by pattern '{matchedPattern}'");
            Skip(null, SkipReason.GlobalBlacklist, $"pattern '{matchedPattern}'", null);
            return WithSkips(FilterResult.GloballyBlacklisted(releaseName, matchedPattern));
        }

        bool alreadyProcessed = await SQLiteHelper.IsReleaseProcessedAsync(releaseName);
        if (alreadyProcessed)
        {
            LogManager.Warning($"Release '{releaseName}' already in database");
            Skip(null, SkipReason.Duplicate, "already in the processed database", null);
            return WithSkips(FilterResult.Duplicate(releaseName));
        }

        if (!InProgressReleases.TryAdd(releaseName, true))
        {
            LogManager.Warning($"Release '{releaseName}' is already being processed");
            Skip(null, SkipReason.Duplicate, "already in flight from another announce", null);
            return WithSkips(FilterResult.Duplicate(releaseName));
        }

        try
        {
            // Snapshot the configs under the lock. LoadAllSiteConfigs/ClearCaches mutate
            // this list from another thread; iterating it directly threw "Collection was
            // modified" mid-race, which was swallowed and silently dropped the release.
            List<JObject> siteConfigsSnapshot;
            lock (configLock)
            {
                siteConfigsSnapshot = allSiteConfigs.ToList();
            }

            if (!siteConfigsSnapshot.Any())
            {
                Skip(null, SkipReason.Error, "no site configurations are loaded", cbftpSection);
                return WithSkips(FilterResult.Error(releaseName, "No site configurations loaded"));
            }

            var allowedSites = new List<string>();

            // Sites where the release group is affiliated: they still race but only
            // as download-only (mirrors the racer's affil handling in CbftpRacer).
            var dlOnlySites = new List<string>();
            var releaseGroup = ExtractGroupFromRelease(releaseName);

            // Pretime is a property of the release, not of the site: query once and
            // reuse for every site's threshold comparison.
            int cachedPretimeDiff = -1;
            bool pretimeQueried = false;

            // Per-call engine: rules are (re)loaded per site immediately before Evaluate.
            var rulesEngine = new RulesEngine();

            foreach (var siteConfig in siteConfigsSnapshot)
            {
                string siteName = siteConfig["site_settings"]?["sitename"]?.ToString();
                bool disableSite = siteConfig["site_settings"]?["disable_site"]?.ToObject<bool>() ?? true;

                if (string.IsNullOrWhiteSpace(siteName) || disableSite)
                {
                    LogManager.Debug($"Skipping site [{siteName}]: disabled or unnamed.");
                    Skip(siteName, SkipReason.SiteDisabled,
                        string.IsNullOrWhiteSpace(siteName) ? "site has no name" : "site is disabled", null);
                    continue;
                }

                try
                {
                    // Skip if not in race_sites of source (except self)
                    // if (!string.Equals(siteName, currentSiteName, StringComparison.OrdinalIgnoreCase))
                    // {
                    //     var sourceSiteConfig = allSiteConfigs.FirstOrDefault(cfg =>
                    //         string.Equals(cfg["site_settings"]?["sitename"]?.ToString(), currentSiteName, StringComparison.OrdinalIgnoreCase));
                    //
                    //     var raceSites = sourceSiteConfig?["race_sites"]?.ToObject<List<string>>() ?? new List<string>();
                    //     if (raceSites.Any() && !raceSites.Contains(siteName, StringComparer.OrdinalIgnoreCase))
                    //     {
                    //         LogManager.Debug($"Skipping [{siteName}]: not in [{currentSiteName}] race_sites list.");
                    //         continue;
                    //     }
                    // }

                    // For Global PreBots, check the ORIGINAL IRC section
                    string ircSectionToCheck;

                    if (!string.IsNullOrEmpty(originalIrcSection))
                    {
                        // Global PreBot mode: check if site has the ORIGINAL IRC section enabled
                        ircSectionToCheck = originalIrcSection;
                        LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}]: Global PreBot mode - checking original IRC section [{LogColors.Green(ircSectionToCheck)}] for release: [{LogColors.Orange(releaseName)}]");
                    }
                    else
                    {
                        // Regular SiteBot mode: use cached reverse mapping
                        if (CbftpToIrcSectionCache.TryGetValue(siteName, out var siteCache) &&
                            siteCache.TryGetValue(cbftpSection, out string mappedIrcSection))
                        {
                            ircSectionToCheck = mappedIrcSection;
                            LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}]: SiteBot mode - CBFTP [{LogColors.Green(cbftpSection)}] maps to IRC [{LogColors.Green(ircSectionToCheck)}] for release: [{LogColors.Orange(releaseName)}]");
                        }
                        else
                        {
                            LogManager.Debug($"[{siteName}]: No IRC section mapping found for CBFTP [{cbftpSection}], skipping");
                            Skip(siteName, SkipReason.NoCbftpMapping,
                                $"no IRC section on this site maps to cbftp section '{cbftpSection}'", cbftpSection);
                            continue;
                        }
                    }

                    // Check if IRC section is enabled
                    var raceSectionsEnabled = siteConfig["race_sections_enabled"]?.ToObject<List<string>>() ?? new List<string>();

                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Debug($"[{siteName}]: race_sections_enabled = [{string.Join(", ", raceSectionsEnabled)}]");
                        LogManager.Debug($"[{siteName}]: Checking if '{ircSectionToCheck}' is in race_sections_enabled...");
                    }

                    if (!IsAllowedSection(ircSectionToCheck, siteConfig))
                    {
                        LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}]: IRC section [{LogColors.Green(ircSectionToCheck)}] NOT enabled in Race Sections, skipping");
                        Skip(siteName, SkipReason.SectionDisabled,
                            $"section '{ircSectionToCheck}' is not in this site's enabled race sections", ircSectionToCheck);
                        continue;
                    }

                    LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}]: IRC section [{LogColors.Green(ircSectionToCheck)}] is enabled ✓");


                    // Check max pretime for THIS site
                    int? maxPretimeSeconds = null;

                    // Priority 1: Section-level pretime
                    var configSection = siteConfig["sections"]?.FirstOrDefault(s =>
                        string.Equals((string)s["irc_name"], ircSectionToCheck, StringComparison.OrdinalIgnoreCase));

                    var releaseSkiplists = configSection?["skiplists"]?.ToObject<List<string>>() ?? new List<string>();
                    if (MatchesReleaseSkiplist(releaseName, releaseSkiplists, out var skipPattern))
                    {
                        LogManager.LogCBFTP(
                            CBFTPEventType.Info,
                            $"[{LogColors.Magenta(siteName)}] Release skiplist matched [{LogColors.Yellow(skipPattern)}], skipping [{LogColors.Orange(releaseName)}]");
                        Skip(siteName, SkipReason.Skiplist,
                            $"section skiplist pattern '{skipPattern}'", ircSectionToCheck);
                        continue;
                    }

                    if (configSection?["pretime"]?.Value<int?>() is int sectionPretime && sectionPretime > 0)
                    {
                        maxPretimeSeconds = sectionPretime;
                    }
                    // Priority 2: Site-level max_pre_time
                    else if (siteConfig["site_settings"]?["max_pre_time"]?.Value<int?>() is int sitePretime && sitePretime > 0)
                    {
                        maxPretimeSeconds = sitePretime;
                    }

                    // If max pretime is configured, check it.
                    // The pretime of a release is the same for every site — only the
                    // threshold differs — so the DB is queried once per release
                    // instead of opening a connection per site.
                    if (maxPretimeSeconds.HasValue && maxPretimeSeconds.Value > 0)
                    {
                        if (!pretimeQueried)
                        {
                            cachedPretimeDiff = await SQLiteHelper.GetPretimeDifferenceSecondsAsync(releaseName);
                            pretimeQueried = true;
                        }

                        int pretimeSeconds = cachedPretimeDiff;
                        // -1 means "no pretime found" → allow (same as CheckMaxPretimeAsync)
                        bool allowed = pretimeSeconds == -1 || pretimeSeconds <= maxPretimeSeconds.Value;

                        if (!allowed)
                        {
                            string pretimeSource = configSection?["pretime"]?.Value<int?>() is int ? $"section [{ircSectionToCheck}]" : "site";
                            LogManager.LogCBFTP(
                                CBFTPEventType.Info,
                                $"[{LogColors.Magenta(siteName)}] Pretime check: BLOCKED - {pretimeSeconds}s exceeds {pretimeSource} max {maxPretimeSeconds}s");
                            Skip(siteName, SkipReason.Pretime,
                                $"{pretimeSeconds}s old, {pretimeSource} allows {maxPretimeSeconds}s", ircSectionToCheck);
                            continue;
                        }

                        if (pretimeSeconds >= 0)
                        {
                            string pretimeSource = configSection?["pretime"]?.Value<int?>() is int ? $"section [{ircSectionToCheck}]" : "site";
                            LogManager.LogCBFTP(
                                CBFTPEventType.Info,
                                $"[{LogColors.Magenta(siteName)}] Pretime check: PASSED - {pretimeSeconds}s < {pretimeSource} max {maxPretimeSeconds}s");
                        }
                        else
                        {
                            LogManager.LogCBFTP(
                                CBFTPEventType.Info,
                                $"[{LogColors.Magenta(siteName)}] Pretime check: No pretime found, ALLOWING");
                        }
                    }


                    // Check IMDB/TVMaze for THIS site
                    if (configSection != null)
                    {
                        // IMDB Check
                        var imdb = configSection["imdb"] as JObject;
                        if (imdb?["enabled"]?.Value<bool>() == true)
                        {
                            LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}] [IMDB] Checking filters");

                            var imdbBlock = await ValidateIMDB(releaseName, imdb, siteName);
                            if (imdbBlock != null)
                            {
                                LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}] [IMDB] Filtered");
                                Skip(siteName, SkipReason.Imdb, imdbBlock, ircSectionToCheck);
                                continue;
                            }

                            LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}] [IMDB] Passed ✓");
                        }

                        // TVMaze Check
                        var tvmaze = configSection["tvmaze"] as JObject;
                        if (tvmaze?["enabled"]?.Value<bool>() == true)
                        {
                            LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}] [TVMaze] Checking filters");

                            var tvmazeBlock = await ValidateTVMaze(releaseName, tvmaze, siteName);
                            if (tvmazeBlock != null)
                            {
                                LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}] [TVMaze] Filtered");
                                Skip(siteName, SkipReason.TvMaze, tvmazeBlock, ircSectionToCheck);
                                continue;
                            }

                            LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}] [TVMaze] Passed ✓");
                        }
                    }

                    // load rules for THIS site using the CBFTP section
                    rulesEngine.LoadRulesForIrcSection(siteConfig, ircSectionToCheck, cbftpSection);

                    // Extract metadata using helpers (TV or Movie)
                    string codec = null, sourceType = null, resolution = null, range = null;
                    string group = null, repeatTag = null;
                    bool isInternal = false, isMulti = false;

                    if (TVMazeHelper.IsTVShow(releaseName))
                    {
                        // TV Show - extract metadata
                        codec = TVMazeHelper.ExtractCodec(releaseName);
                        sourceType = TVMazeHelper.ExtractSource(releaseName);
                        resolution = TVMazeHelper.ExtractResolution(releaseName);
                        range = TVMazeHelper.ExtractRange(releaseName);
                        group = TVMazeHelper.ExtractGroup(releaseName);
                        repeatTag = TVMazeHelper.ExtractRepeatTag(releaseName);
                        isInternal = TVMazeHelper.IsInternal(releaseName);
                        isMulti = TVMazeHelper.IsMulti(releaseName);
                    }
                    else
                    {
                        // Movie - extract metadata
                        codec = IMDBHelper.ExtractCodec(releaseName);
                        sourceType = IMDBHelper.ExtractSource(releaseName);
                        resolution = IMDBHelper.ExtractResolution(releaseName);
                        range = IMDBHelper.ExtractRange(releaseName);
                        group = IMDBHelper.ExtractGroup(releaseName);
                        repeatTag = IMDBHelper.ExtractRepeatTag(releaseName);
                        isInternal = IMDBHelper.IsInternal(releaseName);
                        isMulti = IMDBHelper.IsMulti(releaseName);
                    }

                    var parsedAttributes = ParseReleaseName(releaseName);
                    var input = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "release", releaseName },
                    { "section", ircSectionToCheck }
                };

                    // Add parsed year from ParseReleaseName
                    if (parsedAttributes.TryGetValue("year", out var year)) input["year"] = year;

                    // Use helper-extracted metadata (overrides ParseReleaseName)
                    if (!string.IsNullOrEmpty(group)) input["group"] = group;
                    if (!string.IsNullOrEmpty(resolution)) input["resolution"] = resolution;
                    if (!string.IsNullOrEmpty(resolution)) input["quality"] = resolution; // alias
                    if (!string.IsNullOrEmpty(sourceType)) input["source"] = sourceType;
                    if (!string.IsNullOrEmpty(codec)) input["codec"] = codec;
                    if (!string.IsNullOrEmpty(range)) input["range"] = range;
                    if (!string.IsNullOrEmpty(range)) input["hdr"] = range; // alias
                    if (!string.IsNullOrEmpty(repeatTag)) input["repeat"] = repeatTag;
                    if (!string.IsNullOrEmpty(repeatTag)) input["proper"] = repeatTag; // alias
                    input["internal"] = isInternal.ToString().ToLower();
                    input["multi"] = isMulti.ToString().ToLower();

                    var evaluationResult = rulesEngine.Evaluate(input, cbftpSection);

                    LogManager.LogCBFTP(CBFTPEventType.Info, $"[{LogColors.Magenta(siteName)}] Rule evaluation: [{LogColors.Yellow(evaluationResult)}] for release: [{LogColors.Orange(releaseName)}]");

                    if (string.Equals(evaluationResult, "DROP", StringComparison.OrdinalIgnoreCase))
                    {
                        LogManager.Warning($"[{siteName}] dropped by rules.");
                        Skip(siteName, SkipReason.Rules,
                            $"rule engine returned DROP ({DescribeInput(input)})", ircSectionToCheck);
                        continue;
                    }

                    // Same wildcard semantics as the global blacklist: * and ? are
                    // wildcards (Regex.Escape alone made them literal, so entries like
                    // "*French*" could never match anything).
                    // FirstOrDefault instead of Any: the pattern that matched is the whole
                    // point of the skip feed, and Any() threw it away.
                    var matchedBlacklist = blacklist?.FirstOrDefault(bl =>
                        MatchesBlacklistPattern(releaseName, bl));

                    if (matchedBlacklist != null)
                    {
                        LogManager.Warning($"[{siteName}] blacklisted for [{LogColors.Orange(releaseName)}]");
                        Skip(siteName, SkipReason.Blacklist,
                            $"pattern '{matchedBlacklist}'", ircSectionToCheck);
                        continue;
                    }

                    allowedSites.Add(siteName);

                    // Affil = download-only: if the release group is affiliated with
                    // this site, mark it DL-Only so the UI/test reflects reality.
                    if (!string.IsNullOrEmpty(releaseGroup))
                    {
                        var affils = siteConfig["affils"]?.ToObject<List<string>>();
                        if (affils != null &&
                            affils.Contains(releaseGroup, StringComparer.OrdinalIgnoreCase))
                        {
                            dlOnlySites.Add(siteName);
                        }
                    }

                }
                catch (Exception ex)
                {
                    LogManager.Error($"Exception processing site [{LogColors.Magenta(siteName)}]: {ex.Message}");
                    Skip(siteName, SkipReason.Error, ex.Message, null);
                }
            }

            // CHECK AFTER ALL SITES HAVE BEEN PROCESSED
            if (!allowedSites.Any())
            {
                Skip(null, SkipReason.NoSites,
                    skips.Count > 0
                        ? $"every site was filtered out ({skips.Count} reason(s) above)"
                        : "no site is configured for this section",
                    cbftpSection);

                return WithSkips(FilterResult.NoSites(releaseName, cbftpSection, "All sites were filtered out"));
            }

            if (allowedSites.Count < 2)
            {
                Skip(null, SkipReason.InsufficientSites,
                    $"only {allowedSites.Count} site ({string.Join(", ", allowedSites)}) passed; a race needs 2",
                    cbftpSection);

                return WithSkips(
                    FilterResult.InsufficientSites(releaseName, cbftpSection, allowedSites.Count, allowedSites));
            }

            LogManager.LogCBFTP(CBFTPEventType.Info, $"{allowedSites.Count} site(s) allowed for release [{LogColors.Orange(releaseName)}]: [{LogColors.Magenta(string.Join(", ", allowedSites))}]");

            // Carried on success too: a race that ran on 2 of 6 sites still raises the
            // question of what happened to the other four.
            return WithSkips(FilterResult.Success(releaseName, cbftpSection, allowedSites, dlOnlySites));
        }
        catch (Exception ex)
        {
            LogManager.Error($"Exception in FilterAllowedSites: {ex.Message}");
            Skip(null, SkipReason.Error, ex.Message, cbftpSection);
            return WithSkips(FilterResult.Error(releaseName, ex.Message));
        }
        finally
        {
            InProgressReleases.TryRemove(releaseName, out _);
        }
    }


    /// <summary>
    /// Maps an IRC section to a CBFTP section based on triggers and rules.
    /// </summary>
    public static string GetMappedCbftpSection(
        string ircSection,
        string releaseName,
        JObject siteConfig,
        string sectionPrefix,
        string sectionSuffix)
    {
        try
        {
            if (EngineSettings.DebugEnabled)
            {
                LogManager.Debug($"Mapping IRC section [{ircSection}] for release [{releaseName}]");
            }

            // Strip prefix/suffix
            string strippedSection = ircSection.Trim();
            if (!string.IsNullOrEmpty(sectionPrefix) && strippedSection.StartsWith(sectionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                strippedSection = strippedSection.Substring(sectionPrefix.Length);
            }
            if (!string.IsNullOrEmpty(sectionSuffix) && strippedSection.EndsWith(sectionSuffix, StringComparison.OrdinalIgnoreCase))
            {
                strippedSection = strippedSection.Substring(0, strippedSection.Length - sectionSuffix.Length);
            }

            // For Global PreBots, check merged config for simple mappings
            string prebotName = siteConfig["site_settings"]?["pre_announce"]?.ToString();
            if (IsConfiguredGlobalPreBotMode(prebotName))
            {
                if (EngineSettings.DebugEnabled)
                {
                    LogManager.Debug($"Global PreBot detected - checking merged config for mappings");
                }

                // Find section in merged config
                var section = siteConfig["sections"]?.FirstOrDefault(s =>
                    string.Equals((string)s["irc_name"], strippedSection, StringComparison.OrdinalIgnoreCase));

                if (section != null)
                {
                    // Evaluate each tag's trigger_regex (same as the regular-site path
                    // below) — unconditionally taking tags[0] sent e.g. x265 releases
                    // to the x264 CBFTP section whenever a section had multiple tags.
                    var tags = section["tags"] as JArray;
                    if (tags != null && tags.Any())
                    {
                        string fallback = null;

                        foreach (var tag in tags)
                        {
                            string cbftpSection = tag["map_cbftp_section"]?.ToString();
                            if (string.IsNullOrEmpty(cbftpSection))
                                continue;

                            string triggerRegex = tag["trigger_regex"]?.ToString();

                            if (string.IsNullOrEmpty(triggerRegex))
                            {
                                // tag without trigger = fallback if nothing matches
                                if (fallback == null)
                                    fallback = cbftpSection;
                                continue;
                            }

                            string regexPattern = triggerRegex.Trim();
                            bool isCaseInsensitive = false;

                            var delimited = Regex.Match(regexPattern, @"^/(?<pat>.*)/(?<flags>[a-zA-Z]*)$", RegexOptions.Singleline);
                            if (delimited.Success)
                            {
                                regexPattern = delimited.Groups["pat"].Value;
                                isCaseInsensitive = delimited.Groups["flags"].Value.IndexOf('i') >= 0;
                            }

                            try
                            {
                                RegexOptions options = isCaseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;
                                if (Regex.IsMatch(releaseName, regexPattern, options, RegexSafeTimeout))
                                {
                                    if (EngineSettings.DebugEnabled)
                                    {
                                        LogManager.Debug($"PreBot: Mapped IRC [{strippedSection}] → CBFTP [{cbftpSection}] (trigger '{regexPattern}')");
                                    }
                                    return cbftpSection;
                                }
                            }
                            catch (Exception ex)
                            {
                                LogManager.Error($"PreBot: Invalid trigger_regex '{triggerRegex}': {ex.Message}");
                            }
                        }

                        if (!string.IsNullOrEmpty(fallback))
                        {
                            if (EngineSettings.DebugEnabled)
                            {
                                LogManager.Debug($"PreBot: Mapped IRC [{strippedSection}] → CBFTP [{fallback}] (fallback tag)");
                            }
                            return fallback;
                        }
                    }
                }

                // ❌ NO FALLBACK! Section not configured = don't race it!
                if (EngineSettings.DebugEnabled)
                {
                    LogManager.Warning($"PreBot: IRC section [{strippedSection}] not configured in any site, skipping");
                }
                return null;
            }

            // Find IRC section in config (for regular sites)
            var regularSection = siteConfig["sections"]?.FirstOrDefault(s =>
                string.Equals((string)s["irc_name"], strippedSection, StringComparison.OrdinalIgnoreCase));

            if (regularSection == null)
            {
                if (EngineSettings.DebugEnabled)
                {
                    LogManager.Error($"IRC section [{strippedSection}] not found in configuration");
                }
                return null;
            }

            // Get tags
            var regularTags = regularSection["tags"] as JArray;
            if (regularTags == null || !regularTags.Any())
            {
                if (EngineSettings.DebugEnabled)
                {
                    LogManager.Info($"No tags found, using IRC section [{strippedSection}] as CBFTP section");
                }
                return strippedSection;
            }

            string fallbackCbftpSection = null;

            // Per-call engine, loaded for THIS site's IRC section so the rule-based
            // tag disambiguation below evaluates against the correct (fresh) rules.
            var rulesEngine = new RulesEngine();
            rulesEngine.LoadRulesForIrcSection(siteConfig, strippedSection, strippedSection);

            // Process tags and triggers
            foreach (var tag in regularTags)
            {
                string triggerRegex = tag["trigger_regex"]?.ToString();
                string cbftpSection = tag["map_cbftp_section"]?.ToString();

                if (string.IsNullOrEmpty(cbftpSection))
                {
                    continue;
                }

                // Check trigger regex
                if (!string.IsNullOrEmpty(triggerRegex))
                {
                    // Only strip delimiters/flags when the value is actually in /pattern/
                    // or /pattern/i form. The old code did Trim('/').TrimEnd('i'), which
                    // silently turned a bare pattern like "multi" into "mult".
                    string regexPattern = triggerRegex.Trim();
                    bool isCaseInsensitive = false;

                    var delimited = Regex.Match(regexPattern, @"^/(?<pat>.*)/(?<flags>[a-zA-Z]*)$", RegexOptions.Singleline);
                    if (delimited.Success)
                    {
                        regexPattern = delimited.Groups["pat"].Value;
                        isCaseInsensitive = delimited.Groups["flags"].Value.IndexOf('i') >= 0;
                    }

                    try
                    {
                        RegexOptions options = isCaseInsensitive ? RegexOptions.IgnoreCase : RegexOptions.None;

                        if (!Regex.IsMatch(releaseName, regexPattern, options, RegexSafeTimeout))
                        {
                            if (EngineSettings.DebugEnabled)
                            {
                                LogManager.Debug($"Trigger '{regexPattern}' did NOT match, skipping [{cbftpSection}]");
                            }
                            continue;
                        }

                        if (EngineSettings.DebugEnabled)
                        {
                            LogManager.Success($"Trigger '{regexPattern}' matched! Using CBFTP section [{cbftpSection}]");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager.Error($"Invalid regex '{triggerRegex}': {ex.Message}");
                        continue;
                    }
                }

                // Strict match check
                if (string.Equals(cbftpSection, strippedSection, StringComparison.OrdinalIgnoreCase))
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Success($"Strict section match: [{strippedSection}] -> [{cbftpSection}]");
                    }
                    return cbftpSection;
                }

                // Parse release name to extract attributes
                var parsedAttributes = ParseReleaseName(releaseName);

                // Evaluate rules with README keys
                var input = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "release", releaseName },
                { "section", strippedSection }
            };

                // Add parsed attributes (README keys only)
                if (parsedAttributes.TryGetValue("year", out var year))
                    input["year"] = year;

                if (parsedAttributes.TryGetValue("group", out var group))
                    input["group"] = group;

                if (parsedAttributes.TryGetValue("resolution", out var resolution))
                    input["quality"] = resolution;

                if (parsedAttributes.TryGetValue("source", out var source))
                    input["source"] = source;

                var evaluationResult = rulesEngine.Evaluate(input, cbftpSection);

                if (string.Equals(evaluationResult, "ALLOW", StringComparison.OrdinalIgnoreCase))
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Success($"CBFTP section [{cbftpSection}] allowed by rules");
                    }
                    return cbftpSection;
                }

                if (!string.Equals(evaluationResult, "DROP", StringComparison.OrdinalIgnoreCase) && fallbackCbftpSection == null)
                {
                    fallbackCbftpSection = cbftpSection;
                }
            }

            // Use fallback if available
            //if (!string.IsNullOrEmpty(fallbackCbftpSection))
            //{
            //    if (EngineSettings.DebugEnabled)
            //    {
            //        LogManager.Success($"Using fallback CBFTP section [{fallbackCbftpSection}]");
            //    }
            //    return fallbackCbftpSection;
            //}

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Warning($"No valid CBFTP section found for [{releaseName}]");
            }
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Error($"Exception in GetMappedCbftpSection: {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Extracts the group name from a release name.
    /// Format: Some.Release-GROUP
    /// </summary>
    public static string ExtractGroupFromRelease(string releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
            return string.Empty;

        var match = Regex.Match(releaseName, @"-([A-Z0-9_]+)$", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value : string.Empty;
    }


    /// <summary>
    /// Checks if an IRC section is in the allowed list.
    /// </summary>
    public static bool IsAllowedSection(string section, JObject siteConfig)
    {
        var enabledSections = siteConfig["race_sections_enabled"]?.ToObject<List<string>>() ?? new List<string>();
        return enabledSections.Any(s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase));
    }


    /// <summary>
    /// Parses a release name to extract attributes like year, resolution, source, codec, etc.
    /// Expanded version based on TRD.js release name parser.
    /// </summary>
    public static Dictionary<string, string> ParseReleaseName(string releaseName)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Year
            var yearMatch = Regex.Match(releaseName, @"(19\d{2}|20\d{2})");
            if (yearMatch.Success)
                parsed["year"] = yearMatch.Groups[1].Value;

            // Resolution (expanded)
            var resolutionMatch = Regex.Match(releaseName, @"[\s._-](720P|1080P|1280P|1440P|1920P|2160P|2300P|2700P|2880P)[\s._-]", RegexOptions.IgnoreCase);
            if (resolutionMatch.Success)
                parsed["resolution"] = resolutionMatch.Groups[1].Value.ToUpper();

            // Source (expanded)
            if (Regex.IsMatch(releaseName, @"[\s._-](((720p|1080p)\.(PURE\.)?M?BLURAY)|COMPLETE(\.PURE)?\.M?BLURAY)", RegexOptions.IgnoreCase))
            {
                parsed["source"] = "BLURAY";
            }
            else if (Regex.IsMatch(releaseName, @"[\s._-]((2160p\.UHD\.M?BLURAY)|COMPLETE(\.UHD)?\.M?BLURAY)", RegexOptions.IgnoreCase))
            {
                parsed["source"] = "UHD.BLURAY";
            }
            else
            {
                var sdMatch = Regex.Match(releaseName, @"[\s._-](DVDRIP|BDRIP)", RegexOptions.IgnoreCase);
                if (sdMatch.Success)
                {
                    parsed["source"] = sdMatch.Groups[1].Value.ToUpper();
                }
                else
                {
                    var sourceMatch = Regex.Match(releaseName, @"[\s._-]([AU]?HDTV|AUHDTV|PDTV|DSR|WEBRIP|WEB)[\s._-]", RegexOptions.IgnoreCase);
                    if (sourceMatch.Success)
                        parsed["source"] = sourceMatch.Groups[1].Value.ToUpper();
                }
            }

            // Codec
            var codecMatch = Regex.Match(releaseName, @"[\s._-]([xh]26[45]|xvid|VP[89])[\s._-]", RegexOptions.IgnoreCase);
            if (codecMatch.Success)
                parsed["codec"] = codecMatch.Groups[1].Value.ToUpper();

            // Range (HDR, DV, HLG)
            var rangeMatch = Regex.Match(releaseName, @"[\s._-](DV\.HDR|HDR|DV|HLG)[\s._-]", RegexOptions.IgnoreCase);
            if (rangeMatch.Success)
                parsed["range"] = rangeMatch.Groups[1].Value.ToUpper();

            // Group
            var groupMatch = Regex.Match(releaseName, @"-([A-Z0-9_]+)$", RegexOptions.IgnoreCase);
            if (groupMatch.Success)
                parsed["group"] = groupMatch.Groups[1].Value;

            // Internal detection
            if (Regex.IsMatch(releaseName, @"[\s._-](INTERNAL|INT)[\s._-]", RegexOptions.IgnoreCase) ||
                (parsed.ContainsKey("group") && parsed["group"].ToUpper().EndsWith("_INT")))
            {
                parsed["internal"] = "true";
            }

            // Multi-language detection
            if (releaseName.ToUpper().Contains("MULTI"))
            {
                parsed["multi"] = "true";
            }

            // Language detection (simplified - add more if needed)
            var languageMatch = Regex.Match(releaseName, @"[\s._-](GERMAN|FRENCH|SPANISH|ITALIAN|DUTCH|POLISH|RUSSIAN|JAPANESE|KOREAN|CHINESE|SWEDISH|DANISH|NORWEGIAN|FINNISH)[\s._-]", RegexOptions.IgnoreCase);
            if (languageMatch.Success)
                parsed["language"] = languageMatch.Groups[1].Value.ToUpper();

            // PROPER/REPACK/RERIP detection
            var repeatMatch = Regex.Match(releaseName, @"[\s._-](REAL\.PROPER|PROPER|RERIP|REPACK)[\s._-]", RegexOptions.IgnoreCase);
            if (repeatMatch.Success)
                parsed["repeat"] = repeatMatch.Groups[1].Value.ToUpper();

            // TV episode (multiple formats)
            // Format 1: S01E01-E02
            var episodeMatch1 = Regex.Match(releaseName, @"[\s._-](S\d+E(\d+)-?E(\d+))[\s._-]", RegexOptions.IgnoreCase);
            if (episodeMatch1.Success)
            {
                parsed["season_episode"] = episodeMatch1.Groups[1].Value;
                parsed["episode"] = episodeMatch1.Groups[2].Value + episodeMatch1.Groups[3].Value;
                parsed["type"] = "tv";
            }
            else
            {
                // Format 2: S01E01 or Episode.01
                var episodeMatch2 = Regex.Match(releaseName, @"[\s._-]((?:S\d+)?(?:Episode|E|Part)\.?(\d+))[\s._-]", RegexOptions.IgnoreCase);
                if (episodeMatch2.Success)
                {
                    parsed["episode"] = episodeMatch2.Groups[2].Value;
                    parsed["type"] = "tv";
                }
                else
                {
                    // Format 3: 1x01
                    var episodeMatch3 = Regex.Match(releaseName, @"[\s._-](\d+)x(\d+)[\s._-]", RegexOptions.IgnoreCase);
                    if (episodeMatch3.Success)
                    {
                        parsed["season"] = episodeMatch3.Groups[1].Value;
                        parsed["episode"] = episodeMatch3.Groups[2].Value;
                        parsed["type"] = "tv";
                    }
                    else
                    {
                        // Format 4: Date-based (2024.12.23)
                        var episodeMatch4 = Regex.Match(releaseName, @"[\s._-](\d{4})\.(\d{2}\.\d{2})[\s._-]", RegexOptions.IgnoreCase);
                        if (episodeMatch4.Success)
                        {
                            parsed["season"] = episodeMatch4.Groups[1].Value;
                            parsed["episode"] = episodeMatch4.Groups[2].Value;
                            parsed["type"] = "tv";
                        }
                        else
                        {
                            parsed["type"] = "movie";
                        }
                    }
                }
            }

            // Extract season if not already set (fallback)
            if (!parsed.ContainsKey("season"))
            {
                var seasonMatch = Regex.Match(releaseName, @"[\s._]S(\d+)[\s._E]", RegexOptions.IgnoreCase);
                if (seasonMatch.Success)
                    parsed["season"] = seasonMatch.Groups[1].Value;
            }
        }
        catch (Exception ex)
        {
            if (EngineSettings.DebugEnabled)
            {
                LogManager.Warning($"Error parsing release name '{releaseName}': {ex.Message}");
            }
        }

        return parsed;
    }

    /// <summary>
    /// Gets detailed site information for a CBFTP section (for logging purposes).
    /// Uses cached data, no file I/O.
    /// </summary>
    public static (List<string> AllowedSites, List<string> SkippedSites) GetSiteDetailsForSection(string cbftpSection)
    {
        var allowedSites = new List<string>();
        var allSites = new List<string>();

        lock (configLock)
        {
            foreach (var siteConfig in allSiteConfigs)
            {
                string siteName = siteConfig["site_settings"]?["sitename"]?.ToString();
                if (string.IsNullOrEmpty(siteName))
                    continue;

                allSites.Add(siteName);

                // Use cached lookup
                if (CbftpToIrcSectionCache.TryGetValue(siteName, out var cache) &&
                    cache.TryGetValue(cbftpSection, out string ircSection))
                {
                    // Check if enabled
                    var raceSectionsEnabled = siteConfig["race_sections_enabled"]?.ToObject<List<string>>() ?? new List<string>();
                    if (raceSectionsEnabled.Contains(ircSection, StringComparer.OrdinalIgnoreCase))
                    {
                        allowedSites.Add(siteName);
                    }
                }
            }
        }

        var skippedSites = allSites.Except(allowedSites, StringComparer.OrdinalIgnoreCase).ToList();
        return (allowedSites, skippedSites);
    }


    /// <summary>
    /// Checks a parsed genre list using case-insensitive exact matching.
    /// </summary>
    private static bool HasGenre(IEnumerable<string> genres, string genre)
    {
        return genres?.Any(g => g.Equals(genre, StringComparison.OrdinalIgnoreCase)) == true;
    }

    private static List<string> GetStringList(JObject config, params string[] keys)
    {
        if (config == null)
            return new List<string>();

        foreach (var key in keys)
        {
            var token = config[key];
            if (token == null)
                continue;

            if (token is JArray)
                return token.ToObject<List<string>>() ?? new List<string>();

            var single = token.ToString();
            if (!string.IsNullOrWhiteSpace(single))
            {
                return single
                    .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList();
            }
        }

        return new List<string>();
    }

    /// <summary>
    /// Validates release against IMDB filters
    /// </summary>
    /// <summary>
    /// Checks a release against the section's IMDB filters.
    /// Returns null when it passes, otherwise the reason it was blocked — the reason is
    /// what makes the skip feed useful, and a bare bool threw it away.
    /// </summary>
    private static async Task<string> ValidateIMDB(string releaseName, JObject config, string siteName)
    {
        try
        {
            var releaseInfo = await IMDBHelper.EnrichReleaseInfo(releaseName);

            if (releaseInfo?.Movie == null)
            {
                bool fallback = config["fallback_on_error"]?.Value<bool>() ?? true;
                if (!fallback)
                {
                    LogManager.Warning($"[{siteName}] [IMDB] ❌ No data found, blocking");
                }
                return fallback ? null : "no metadata found for this release";
            }

            var movie = releaseInfo.Movie;
            var ratingText = movie.ImdbRating.HasValue ? movie.ImdbRating.Value.ToString("F1") : "N/A";
            var votesText = movie.ImdbVotes.HasValue ? movie.ImdbVotes.Value.ToString("N0") : "N/A";
            var sourceText = string.IsNullOrWhiteSpace(movie.DataSource) ? "IMDb" : movie.DataSource;
            LogManager.Info($"[{siteName}] [IMDB] {movie.Title} ({movie.Year}) - Rating: {ratingText}/10 ({votesText} votes) - Source: {sourceText}");

            double minRating = config["min_rating"]?.Value<double>() ?? 0;
            if (minRating > 0 && !movie.ImdbRating.HasValue)
            {
                LogManager.Warning($"[{siteName}] [IMDB] ❌ No rating available while minimum {minRating} is configured");
                return $"No rating available while minimum {minRating} is configured";
            }
            if (minRating > 0 && movie.ImdbRating.HasValue && movie.ImdbRating.Value < minRating)
            {
                LogManager.Warning($"[{siteName}] [IMDB] ❌ Rating {movie.ImdbRating.Value} < {minRating}");
                return $"Rating {movie.ImdbRating.Value} < {minRating}";
            }

            int minVotes = config["min_votes"]?.Value<int>() ?? 0;
            if (minVotes > 0 && !movie.ImdbVotes.HasValue)
            {
                LogManager.Warning($"[{siteName}] [IMDB] ❌ No vote count available while minimum {minVotes:N0} is configured");
                return $"No vote count available while minimum {minVotes:N0} is configured";
            }
            if (minVotes > 0 && movie.ImdbVotes.HasValue && movie.ImdbVotes.Value < minVotes)
            {
                LogManager.Warning($"[{siteName}] [IMDB] ❌ Votes {movie.ImdbVotes.Value:N0} < {minVotes:N0}");
                return $"Votes {movie.ImdbVotes.Value:N0} < {minVotes:N0}";
            }

            if (config["only_english"]?.Value<bool>() == true)
            {
                if (movie.Languages == null || !movie.Languages.Any(l => l.Equals("English", StringComparison.OrdinalIgnoreCase)))
                {
                    LogManager.Warning($"[{siteName}] [IMDB] ❌ Not English ({movie.Language})");
                    return $"Not English ({movie.Language})";
                }
            }

            if (config["only_us_country"]?.Value<bool>() == true)
            {
                if (movie.Countries == null || !movie.Countries.Any(c => c.ToLower().Contains("united states")))
                {
                    LogManager.Warning($"[{siteName}] [IMDB] ❌ Not US ({movie.Country})");
                    return $"Not US ({movie.Country})";
                }
            }

            var allowedGenres = config["allowed_genres"]?.ToObject<List<string>>() ?? new List<string>();
            if (allowedGenres.Any())
            {
                if (movie.Genres == null || !movie.Genres.Any())
                {
                    LogManager.Warning($"[{siteName}] [IMDB] ❌ No genre data available while genre allow-list is configured");
                    return $"No genre data available while genre allow-list is configured";
                }

                if (!movie.Genres.Any(g => allowedGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                {
                    LogManager.Warning($"[{siteName}] [IMDB] ❌ Genre not allowed: {string.Join(", ", movie.Genres)}");
                    return $"Genre not allowed: {string.Join(", ", movie.Genres)}";
                }
            }

            var blockedGenres = config["blocked_genres"]?.ToObject<List<string>>() ?? new List<string>();
            if (blockedGenres.Any() && movie.Genres != null)
            {
                if (movie.Genres.Any(g => blockedGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                {
                    LogManager.Warning($"[{siteName}] [IMDB] ❌ Genre blocked: {string.Join(", ", movie.Genres)}");
                    return $"Genre blocked: {string.Join(", ", movie.Genres)}";
                }
            }

            if (config["no_documentary"]?.Value<bool>() == true && HasGenre(movie.Genres, "Documentary"))
            {
                LogManager.Warning($"[{siteName}] [IMDB] ❌ Documentary genre blocked");
                return $"Documentary genre blocked";
            }

            if (config["no_music"]?.Value<bool>() == true &&
                (HasGenre(movie.Genres, "Music") || HasGenre(movie.Genres, "Musical")))
            {
                LogManager.Warning($"[{siteName}] [IMDB] ❌ Music/Musical genre blocked");
                return $"Music/Musical genre blocked";
            }

            if (config["no_comedy"]?.Value<bool>() == true && HasGenre(movie.Genres, "Comedy"))
            {
                LogManager.Warning($"[{siteName}] [IMDB] ❌ Comedy genre blocked");
                return $"Comedy genre blocked";
            }

            if (config["no_show"]?.Value<bool>() == true &&
                !string.Equals(movie.Type, "movie", StringComparison.OrdinalIgnoreCase))
            {
                LogManager.Warning($"[{siteName}] [IMDB] ❌ IMDb title type '{movie.Type}' blocked by movies-only filter");
                return $"IMDb title type '{movie.Type}' blocked by movies-only filter";
            }

            return null;
        }
        catch (Exception ex)
        {
            LogManager.Error($"[{siteName}] [IMDB] ERROR: {ex.Message}");
            bool fallback = config["fallback_on_error"]?.Value<bool>() ?? true;
            return fallback ? null : $"IMDB lookup failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Validates release against TVMaze filters.
    /// Returns null when it passes, otherwise the reason it was blocked.
    /// </summary>
    private static async Task<string> ValidateTVMaze(string releaseName, JObject config, string siteName)
    {
        try
        {
            var cacheDays = config["cache_duration_days"]?.Value<int>() ?? 7;
            if (cacheDays < 1) cacheDays = 7;

            var releaseInfo = await TVMazeHelper.EnrichReleaseInfo(releaseName, cacheDays);

            if (releaseInfo?.Show == null)
            {
                bool fallback = config["fallback_on_error"]?.Value<bool>() ?? true;
                if (!fallback)
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ No data found, blocking");
                }
                return fallback ? null : "no metadata found for this release";
            }

            var show = releaseInfo.Show;
            var rating = show.Rating?.Average?.ToString("F1") ?? "N/A";
            LogManager.Info($"[{siteName}] [TVMaze] {show.Name} - {show.Status} - Rating: {rating}");

            if (config["skip_ended_shows"]?.Value<bool>() == true && show.Status == "Ended")
            {
                LogManager.Warning($"[{siteName}] [TVMaze] ❌ Show has ended");
                return $"Show has ended";
            }

            double minRating = config["min_rating"]?.Value<double>() ?? 0;
            if (minRating > 0 && show.Rating?.Average == null)
            {
                LogManager.Warning($"[{siteName}] [TVMaze] ❌ No rating available while minimum {minRating} is configured");
                return $"No rating available while minimum {minRating} is configured";
            }
            if (minRating > 0 && show.Rating?.Average != null && show.Rating.Average < minRating)
            {
                LogManager.Warning($"[{siteName}] [TVMaze] ❌ Rating {show.Rating.Average:F1} < {minRating}");
                return $"Rating {show.Rating.Average:F1} < {minRating}";
            }

            var allowedGenres = config["allowed_genres"]?.ToObject<List<string>>() ?? new List<string>();
            if (allowedGenres.Any())
            {
                if (show.Genres == null || !show.Genres.Any())
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ No genre data available while genre allow-list is configured");
                    return $"No genre data available while genre allow-list is configured";
                }

                if (!show.Genres.Any(g => allowedGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ Genre not allowed: {string.Join(", ", show.Genres)}");
                    return $"Genre not allowed: {string.Join(", ", show.Genres)}";
                }
            }

            var blockedGenres = config["blocked_genres"]?.ToObject<List<string>>() ?? new List<string>();
            if (blockedGenres.Any() && show.Genres != null)
            {
                if (show.Genres.Any(g => blockedGenres.Contains(g, StringComparer.OrdinalIgnoreCase)))
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ Genre blocked: {string.Join(", ", show.Genres)}");
                    return $"Genre blocked: {string.Join(", ", show.Genres)}";
                }
            }

            var allowedNetworks = config["allowed_networks"]?.ToObject<List<string>>() ?? new List<string>();
            if (allowedNetworks.Any())
            {
                var network = show.Network?.Name ?? show.WebChannel?.Name ?? "";
                if (!allowedNetworks.Contains(network, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ Network '{network}' not allowed");
                    return $"Network '{network}' not allowed";
                }
            }

            var allowedShowTypes = GetStringList(config, "allowed_show_types", "show_types", "allowed_types", "allowed_showtypes");
            if (allowedShowTypes.Any())
            {
                var showType = show.Type ?? "";
                if (string.IsNullOrWhiteSpace(showType))
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ Show type missing while allowed types are configured: {string.Join(", ", allowedShowTypes)}");
                    return $"Show type missing while allowed types are configured: {string.Join(", ", allowedShowTypes)}";
                }

                if (!allowedShowTypes.Contains(showType, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ Show type '{showType}' not allowed");
                    return $"Show type '{showType}' not allowed";
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            LogManager.Error($"[{siteName}] [TVMaze] ERROR: {ex.Message}");
            bool fallback = config["fallback_on_error"]?.Value<bool>() ?? true;
            return fallback ? null : $"TVMaze lookup failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Clears all caches. Call this when reloading configurations.
    /// </summary>
    public static void ClearCaches()
    {
        lock (configLock)
        {
            allSiteConfigs.Clear();
            SiteBasedCache.Clear();
            CbftpToIrcSectionCache.Clear();
            InProgressReleases.Clear();
        }

        lock (blacklistLock)
        {
            globalBlacklist.Clear();
        }

        LogManager.Info("All caches cleared");
    }
}

/// <summary>
/// Represents a mapped tag with precompiled regex for performance.
/// </summary>
public class MappedTag
{
    public string CbftpSection { get; set; }
    public Regex CompiledRegex { get; set; }
    public List<string> CbftpRules { get; set; }
}
