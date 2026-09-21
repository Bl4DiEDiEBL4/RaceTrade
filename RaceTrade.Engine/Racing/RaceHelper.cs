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
    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> FxpBackendToIrcSectionCache = new();
    private static readonly ConcurrentDictionary<string, bool> InProgressReleases = new();
    private static readonly List<JObject> allSiteConfigs = new List<JObject>();
    private static readonly object configLock = new();
    private static readonly string[] ReservedSiteConfigNames = { "new_site", "template", "example" };
    private const string ConfigKeyProperty = "_racetrade_config_key";

    /// <summary>
    /// Guard against catastrophic backtracking in user/config supplied patterns.
    /// Without this a pathological trigger/blacklist regex can hang the filter thread.
    /// </summary>
    private static readonly TimeSpan RegexSafeTimeout = TimeSpan.FromMilliseconds(250);
    // NOTE: RulesEngine holds per-evaluation state (_sectionRules/_tagRules), so it is
    // NOT shared statically anymore — each racing path creates its own instance and
    // loads rules immediately before evaluating (avoids stale rules + concurrency races).

    /// <summary>
    /// Precompiled release-name regexes (mirrors TRD.js' precompiled EPISODE_FORM/etc.
    /// statics). The static Regex.* methods only cache ~15 patterns process-wide; this
    /// codebase uses far more, so the inline calls in ParseReleaseName were re-parsing
    /// their patterns on every announce for every site.
    /// </summary>
    private static class Rx
    {
        private const RegexOptions CI = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

        public static readonly Regex Year = new(@"(19\d{2}|20\d{2})", RegexOptions.Compiled);
        public static readonly Regex Resolution = new(@"[\s._-](720P|1080P|1280P|1440P|1920P|2160P|2300P|2700P|2880P)[\s._-]", CI);
        public static readonly Regex SourceBluray = new(@"[\s._-](((720p|1080p)\.(PURE\.)?M?BLURAY)|COMPLETE(\.PURE)?\.M?BLURAY)", CI);
        public static readonly Regex SourceUhdBluray = new(@"[\s._-]((2160p\.UHD\.M?BLURAY)|COMPLETE(\.UHD)?\.M?BLURAY)", CI);
        public static readonly Regex SourceSd = new(@"[\s._-](DVDRIP|BDRIP)", CI);
        public static readonly Regex SourceTv = new(@"[\s._-]([AU]?HDTV|AUHDTV|PDTV|DSR|WEBRIP|WEB)[\s._-]", CI);
        public static readonly Regex Codec = new(@"[\s._-]([xh]26[45]|xvid|VP[89])[\s._-]", CI);
        // HDR.DV added for parity with TRD.js (which normalizes it to DV.HDR).
        public static readonly Regex Range = new(@"[\s._-](DV\.HDR|HDR\.DV|HDR|DV|HLG)[\s._-]", CI);
        public static readonly Regex Group = new(@"-([A-Z0-9_]+)$", CI);
        public static readonly Regex Internal = new(@"[\s._-](INTERNAL|INT)[\s._-]", CI);
        public static readonly Regex Language = new(@"[\s._-](GERMAN|FRENCH|SPANISH|ITALIAN|DUTCH|POLISH|RUSSIAN|JAPANESE|KOREAN|CHINESE|SWEDISH|DANISH|NORWEGIAN|FINNISH)[\s._-]", CI);
        public static readonly Regex Repeat = new(@"[\s._-](REAL\.PROPER|PROPER|RERIP|REPACK)[\s._-]", CI);
        public static readonly Regex Episode1 = new(@"[\s._-](S\d+E(\d+)-?E(\d+))[\s._-]", CI);
        public static readonly Regex Episode2 = new(@"[\s._-]((?:S\d+)?(?:Episode|E|Part)\.?(\d+))[\s._-]", CI);
        public static readonly Regex Episode3 = new(@"[\s._-](\d+)x(\d+)[\s._-]", CI);
        public static readonly Regex Episode4 = new(@"[\s._-](\d{4})\.(\d{2}\.\d{2})[\s._-]", CI);
        public static readonly Regex SeasonFallback = new(@"[\s._]S(\d+)[\s._E]", CI);
        public static readonly Regex DelimitedTrigger = new(@"^/(?<pat>.*)/(?<flags>[a-zA-Z]*)$", RegexOptions.Singleline | RegexOptions.Compiled);
        public static readonly Regex GlobalPreBotMode = new(@"^Global\s+PreBot\s*\(([^)]+)\)\s*$", CI);
    }

    // Global blacklist (set from MainApp)
    private static List<string> globalBlacklist = new List<string>();
    // Patterns compiled once at SetGlobalBlacklist time instead of being rebuilt
    // (and re-parsed by the regex engine) on every single announce.
    private static List<(string Pattern, Regex Regex)> globalBlacklistCompiled = new();
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

            // Compile once here instead of on every announce. Invalid patterns are
            // logged once at config time and skipped, instead of erroring per release.
            var compiled = new List<(string, Regex)>(globalBlacklist.Count);
            foreach (var pattern in globalBlacklist)
            {
                if (string.IsNullOrWhiteSpace(pattern))
                    continue;

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

                    compiled.Add((pattern, new Regex(regexPattern,
                        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
                        RegexSafeTimeout)));
                }
                catch (Exception ex)
                {
                    LogManager.Error($"Invalid global blacklist pattern '{pattern}': {ex.Message}");
                }
            }

            globalBlacklistCompiled = compiled;
        }
    }

    /// <summary>
    /// Checks if a release matches any global blacklist pattern.
    /// </summary>
    private static bool IsGloballyBlacklisted(string releaseName, out string matchedPattern)
    {
        matchedPattern = null;

        // Snapshot under the lock, match outside it: the compiled list is replaced
        // wholesale by SetGlobalBlacklist, never mutated in place.
        List<(string Pattern, Regex Regex)> compiled;
        lock (blacklistLock)
        {
            compiled = globalBlacklistCompiled;
        }

        foreach (var (pattern, regex) in compiled)
        {
            try
            {
                if (regex.IsMatch(releaseName))
                {
                    matchedPattern = pattern;
                    return true;
                }
            }
            catch (RegexMatchTimeoutException)
            {
                LogManager.Error($"Global blacklist pattern '{pattern}' timed out on '{releaseName}'");
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
    /// release name, not FXP backend file or directory names.
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

        var match = Rx.GlobalPreBotMode.Match(mode);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value);
    }

    private sealed class FxpBackendIndex
    {
        public bool ConfigLoaded { get; set; }
        public HashSet<string> KnownRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ActiveRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // The backend config only changes on edits, so cache the parsed index by
    // file write time instead of re-reading and re-parsing it on every announce.
    private static readonly object FxpBackendIndexLock = new();
    private static FxpBackendIndex cachedFxpBackendIndex;
    private static string cachedFxpBackendIndexPath;
    private static DateTime cachedFxpBackendIndexWriteUtc;

    private static void CacheFxpBackendIndex(FxpBackendIndex index, string configPath, DateTime writeUtc)
    {
        if (writeUtc == DateTime.MinValue)
            return;

        lock (FxpBackendIndexLock)
        {
            cachedFxpBackendIndex = index;
            cachedFxpBackendIndexPath = configPath;
            cachedFxpBackendIndexWriteUtc = writeUtc;
        }
    }

    private static FxpBackendIndex LoadFxpBackendIndex()
    {
        var index = new FxpBackendIndex();

        if (!FxpBackendConfigFiles.TryGetReadablePath(out var configPath))
            return index;

        DateTime writeUtc;
        try
        {
            writeUtc = File.GetLastWriteTimeUtc(configPath);
        }
        catch
        {
            writeUtc = DateTime.MinValue;
        }

        if (writeUtc != DateTime.MinValue)
        {
            lock (FxpBackendIndexLock)
            {
                if (cachedFxpBackendIndex != null &&
                    writeUtc == cachedFxpBackendIndexWriteUtc &&
                    string.Equals(cachedFxpBackendIndexPath, configPath, StringComparison.OrdinalIgnoreCase))
                {
                    return cachedFxpBackendIndex;
                }
            }
        }

        try
        {
            var config = JObject.Parse(File.ReadAllText(configPath));
            var servers = ReadFxpBackendValue(config, FxpBackendJsonKeys.Backends, FxpBackendJsonKeys.LegacyBackends) as JArray;
            index.ConfigLoaded = true;

            if (servers == null)
            {
                CacheFxpBackendIndex(index, configPath, writeUtc);
                return index;
            }

            foreach (var server in servers.OfType<JObject>())
            {
                var disabled = server["disabled"]?.Value<bool>() ?? false;
                var id = server["id"]?.ToString();
                var name = server["name"]?.ToString();

                AddFxpBackendRef(index.KnownRefs, id);
                AddFxpBackendRef(index.KnownRefs, name);

                if (!disabled)
                {
                    AddFxpBackendRef(index.ActiveRefs, id);
                    AddFxpBackendRef(index.ActiveRefs, name);
                }
            }

            // Cache successes only; a failed read is retried on the next call,
            // exactly like the previous uncached behavior.
            CacheFxpBackendIndex(index, configPath, writeUtc);
        }
        catch (Exception ex)
        {
            LogManager.Warning($"Could not read FXP backend server status from '{configPath}': {ex.Message}");
        }

        return index;
    }

    private static void AddFxpBackendRef(HashSet<string> refs, string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            refs.Add(value.Trim());
    }

    private static bool SiteUsesEnabledFxpBackend(JObject siteConfig, FxpBackendIndex fxpBackendServers, out string reason)
    {
        reason = null;

        if (fxpBackendServers == null || !fxpBackendServers.ConfigLoaded)
            return true;

        if (fxpBackendServers.ActiveRefs.Count == 0)
        {
            reason = "no enabled FXP backend server is available";
            return false;
        }

        var configured = ReadFxpBackendValue(siteConfig["site_settings"], FxpBackendJsonKeys.BackendId, FxpBackendJsonKeys.LegacyBackendId)
            ?.ToString()
            ?.Trim();
        if (string.IsNullOrWhiteSpace(configured))
            return true;

        if (fxpBackendServers.ActiveRefs.Contains(configured))
            return true;

        reason = fxpBackendServers.KnownRefs.Contains(configured)
            ? $"FXP backend server '{configured}' is disabled"
            : $"FXP backend server '{configured}' is missing or disabled";

        return false;
    }

    private static JToken ReadFxpBackendValue(JToken token, string key, string legacyKey) =>
        token?[key] ?? token?[legacyKey];

    private static string ReadMappedFxpBackendSection(JToken tag) =>
        ReadFxpBackendValue(tag, FxpBackendJsonKeys.SectionMap, FxpBackendJsonKeys.LegacySectionMap)?.ToString();

    private static string GetSiteConfigKey(JObject siteConfig)
    {
        var key = siteConfig?[ConfigKeyProperty]?.ToString()?.Trim();
        if (!string.IsNullOrWhiteSpace(key))
            return key;

        return GetRemoteSiteName(siteConfig);
    }

    private static string GetRemoteSiteName(JObject siteConfig) =>
        siteConfig?["site_settings"]?["sitename"]?.ToString()?.Trim() ?? "";

    private static string GetSiteDisplayName(JObject siteConfig)
    {
        var key = GetSiteConfigKey(siteConfig);
        var remote = GetRemoteSiteName(siteConfig);
        if (string.IsNullOrWhiteSpace(remote))
            return key;
        if (string.IsNullOrWhiteSpace(key) || string.Equals(remote, key, StringComparison.OrdinalIgnoreCase))
            return remote;
        return $"{remote} ({key})";
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
            FxpBackendToIrcSectionCache.Clear();

            var directory = "sites";
            var fxpBackendServers = LoadFxpBackendIndex();

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

                    siteConfig[ConfigKeyProperty] = siteConfigName;

                    string siteName = GetSiteDisplayName(siteConfig);
                    bool disableSite = siteConfig["site_settings"]?["disable_site"]?.ToObject<bool>() ?? true;

                    if (disableSite)
                    {
                        LogManager.Warning($"Site [{siteName}] is disabled. Skipping.");
                        continue;
                    }

                    if (!SiteUsesEnabledFxpBackend(siteConfig, fxpBackendServers, out var fxpBackendSkipReason))
                    {
                        LogManager.Warning($"Site [{siteName}] is skipped: {fxpBackendSkipReason}.");
                        continue;
                    }

                    allSiteConfigs.Add(siteConfig);

                    // Build section cache for this site
                    BuildSectionCache(siteConfig, siteConfigName);

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
    /// Builds a fast O(1) lookup cache for FXP backend -> IRC section mappings.
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
                        var fxpBackendSection = ReadMappedFxpBackendSection(tag);
                        if (!string.IsNullOrEmpty(fxpBackendSection))
                        {
                            // Map FXP backend section -> IRC section
                            cache[fxpBackendSection] = ircName;
                        }
                    }
                }
            }
        }

        FxpBackendToIrcSectionCache[siteName] = cache;

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

        var interesting = new[] { "group", "resolution", "source", "codec", "range", "year", "language", "lang", "repeat" };

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
        string fxpBackendSection,
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
                releaseName, reason, detail, site, section ?? fxpBackendSection, currentSiteName);
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
                Skip(null, SkipReason.Error, "no site configurations are loaded", fxpBackendSection);
                return WithSkips(FilterResult.Error(releaseName, "No site configurations loaded"));
            }

            var fxpBackendServers = LoadFxpBackendIndex();
            var allowedSites = new List<string>();

            // Sites where the release group is affiliated: they still race but only
            // as download-only (mirrors the racer's affil handling in FxpBackendRacer).
            var dlOnlySites = new List<string>();
            var releaseGroup = ExtractGroupFromRelease(releaseName);

            // Pretime is a property of the release, not of the site: query once and
            // reuse for every site's threshold comparison.
            int cachedPretimeDiff = -1;
            bool pretimeQueried = false;

            // Per-call engine: rules are (re)loaded per site immediately before Evaluate.
            var rulesEngine = new RulesEngine();

            // Base rule input shared by every site; the per-site copy only swaps
            // the section. Built once here (see BuildRuleInput) so the Test release
            // tools evaluate against exactly the same attributes the racer does.
            var baseInput = BuildRuleInput(releaseName);

            foreach (var siteConfig in siteConfigsSnapshot)
            {
                string siteName = GetSiteConfigKey(siteConfig);
                string siteLogName = GetSiteDisplayName(siteConfig);
                bool disableSite = siteConfig["site_settings"]?["disable_site"]?.ToObject<bool>() ?? true;

                if (string.IsNullOrWhiteSpace(siteName) || disableSite)
                {
                    LogManager.Debug($"Skipping site [{siteLogName}]: disabled or unnamed.");
                    Skip(siteName, SkipReason.SiteDisabled,
                        string.IsNullOrWhiteSpace(siteName) ? "site has no name" : "site is disabled", null);
                    continue;
                }

                if (!SiteUsesEnabledFxpBackend(siteConfig, fxpBackendServers, out var fxpBackendSkipReason))
                {
                    LogManager.LogFxpBackend(
                        FxpBackendEventType.Info,
                        $"[{LogColors.Magenta(siteName)}] skipped: {fxpBackendSkipReason}");
                    Skip(siteName, SkipReason.SiteDisabled, fxpBackendSkipReason, null);
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
                        LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}]: Global PreBot mode - checking original IRC section [{LogColors.Green(ircSectionToCheck)}] for release: [{LogColors.Orange(releaseName)}]");
                    }
                    else
                    {
                        // Regular SiteBot mode: use cached reverse mapping
                        if (FxpBackendToIrcSectionCache.TryGetValue(siteName, out var siteCache) &&
                            siteCache.TryGetValue(fxpBackendSection, out string mappedIrcSection))
                        {
                            ircSectionToCheck = mappedIrcSection;
                            LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}]: SiteBot mode - FXP backend [{LogColors.Green(fxpBackendSection)}] maps to IRC [{LogColors.Green(ircSectionToCheck)}] for release: [{LogColors.Orange(releaseName)}]");
                        }
                        else
                        {
                            LogManager.Debug($"[{siteName}]: No IRC section mapping found for FXP backend [{fxpBackendSection}], skipping");
                            Skip(siteName, SkipReason.NoFxpBackendMapping,
                                $"no IRC section on this site maps to FXP backend section '{fxpBackendSection}'", fxpBackendSection);
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
                        LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}]: IRC section [{LogColors.Green(ircSectionToCheck)}] NOT enabled in Race Sections, skipping");
                        Skip(siteName, SkipReason.SectionDisabled,
                            $"section '{ircSectionToCheck}' is not in this site's enabled race sections", ircSectionToCheck);
                        continue;
                    }

                    LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}]: IRC section [{LogColors.Green(ircSectionToCheck)}] is enabled ✓");


                    // Check max pretime for THIS site
                    int? maxPretimeSeconds = null;

                    // Priority 1: Section-level pretime
                    var configSection = siteConfig["sections"]?.FirstOrDefault(s =>
                        string.Equals((string)s["irc_name"], ircSectionToCheck, StringComparison.OrdinalIgnoreCase));

                    var releaseSkiplists = configSection?["skiplists"]?.ToObject<List<string>>() ?? new List<string>();
                    if (MatchesReleaseSkiplist(releaseName, releaseSkiplists, out var skipPattern))
                    {
                        LogManager.LogFxpBackend(
                            FxpBackendEventType.Info,
                            $"[{LogColors.Magenta(siteName)}] Release skiplist matched [{LogColors.Yellow(skipPattern)}], skipping [{LogColors.Orange(releaseName)}]");
                        Skip(siteName, SkipReason.Skiplist,
                            $"section skiplist pattern '{skipPattern}'", ircSectionToCheck);
                        continue;
                    }

                    // Affil-only section: never race (upload) here. Only an affil-group
                    // release is allowed, and it goes download-only via the block below.
                    bool sectionAffilOnly = configSection?["affil_only"]?.Value<bool>() ?? false;
                    if (sectionAffilOnly)
                    {
                        var affilList = siteConfig["affils"]?.ToObject<List<string>>() ?? new List<string>();
                        if (!GroupIsAffil(releaseGroup, affilList))
                        {
                            var shownGroup = string.IsNullOrEmpty(releaseGroup) ? "unknown" : releaseGroup;
                            LogManager.LogFxpBackend(
                                FxpBackendEventType.Info,
                                $"[{LogColors.Magenta(siteName)}] Section [{ircSectionToCheck}] is affil-only; group [{shownGroup}] is not an affil, skipping.");
                            Skip(siteName, SkipReason.Rules,
                                $"section '{ircSectionToCheck}' is affil-only and group '{shownGroup}' is not an affil", ircSectionToCheck);
                            continue;
                        }
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
                            LogManager.LogFxpBackend(
                                FxpBackendEventType.Info,
                                $"[{LogColors.Magenta(siteName)}] Pretime check: BLOCKED - {pretimeSeconds}s exceeds {pretimeSource} max {maxPretimeSeconds}s");
                            Skip(siteName, SkipReason.Pretime,
                                $"{pretimeSeconds}s old, {pretimeSource} allows {maxPretimeSeconds}s", ircSectionToCheck);
                            continue;
                        }

                        if (pretimeSeconds >= 0)
                        {
                            string pretimeSource = configSection?["pretime"]?.Value<int?>() is int ? $"section [{ircSectionToCheck}]" : "site";
                            LogManager.LogFxpBackend(
                                FxpBackendEventType.Info,
                                $"[{LogColors.Magenta(siteName)}] Pretime check: PASSED - {pretimeSeconds}s < {pretimeSource} max {maxPretimeSeconds}s");
                        }
                        else
                        {
                            LogManager.LogFxpBackend(
                                FxpBackendEventType.Info,
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
                            LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}] [IMDB] Checking filters");

                            var imdbBlock = await ValidateIMDB(releaseName, imdb, siteName);
                            if (imdbBlock != null)
                            {
                                LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}] [IMDB] Filtered");
                                Skip(siteName, SkipReason.Imdb, imdbBlock, ircSectionToCheck);
                                continue;
                            }

                            LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}] [IMDB] Passed ✓");
                        }

                        // TVMaze Check
                        var tvmaze = configSection["tvmaze"] as JObject;
                        if (tvmaze?["enabled"]?.Value<bool>() == true)
                        {
                            LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}] [TVMaze] Checking filters");

                            var tvmazeBlock = await ValidateTVMaze(releaseName, tvmaze, siteName);
                            if (tvmazeBlock != null)
                            {
                                LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}] [TVMaze] Filtered");
                                Skip(siteName, SkipReason.TvMaze, tvmazeBlock, ircSectionToCheck);
                                continue;
                            }

                            LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}] [TVMaze] Passed ✓");
                        }
                    }

                    // load rules for THIS site using the FXP backend section
                    rulesEngine.LoadRulesForIrcSection(siteConfig, ircSectionToCheck, fxpBackendSection);

                    // Metadata was parsed once before the loop; only the section
                    // differs per site.
                    var input = new Dictionary<string, string>(baseInput, StringComparer.OrdinalIgnoreCase)
                    {
                        ["section"] = ircSectionToCheck
                    };

                    var evaluationResult = rulesEngine.Evaluate(input, fxpBackendSection);

                    LogManager.LogFxpBackend(FxpBackendEventType.Info, $"[{LogColors.Magenta(siteName)}] Rule evaluation: [{LogColors.Yellow(evaluationResult)}] for release: [{LogColors.Orange(releaseName)}]");

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
                    // The release must clear the announcing site's blacklist (passed in)
                    // AND this target site's own Blacklist tab. Only the announcing list
                    // was checked before, so a pattern set on the target site was ignored
                    // and the release got raced there anyway (and nuked).
                    var targetBlacklist = siteConfig["global_blacklist"]?.ToObject<List<string>>();
                    var matchedBlacklist =
                        blacklist?.FirstOrDefault(bl => MatchesBlacklistPattern(releaseName, bl))
                        ?? targetBlacklist?.FirstOrDefault(bl => MatchesBlacklistPattern(releaseName, bl));

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
                        if (GroupIsAffil(releaseGroup, affils))
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
                    fxpBackendSection);

                return WithSkips(FilterResult.NoSites(releaseName, fxpBackendSection, "All sites were filtered out"));
            }

            if (allowedSites.Count < 2)
            {
                Skip(null, SkipReason.InsufficientSites,
                    $"only {allowedSites.Count} site ({string.Join(", ", allowedSites)}) passed; a race needs 2",
                    fxpBackendSection);

                return WithSkips(
                    FilterResult.InsufficientSites(releaseName, fxpBackendSection, allowedSites.Count, allowedSites));
            }

            LogManager.LogFxpBackend(FxpBackendEventType.Info, $"{allowedSites.Count} site(s) allowed for release [{LogColors.Orange(releaseName)}]: [{LogColors.Magenta(string.Join(", ", allowedSites))}]");

            // Carried on success too: a race that ran on 2 of 6 sites still raises the
            // question of what happened to the other four.
            return WithSkips(FilterResult.Success(releaseName, fxpBackendSection, allowedSites, dlOnlySites));
        }
        catch (Exception ex)
        {
            LogManager.Error($"Exception in FilterAllowedSites: {ex.Message}");
            Skip(null, SkipReason.Error, ex.Message, fxpBackendSection);
            return WithSkips(FilterResult.Error(releaseName, ex.Message));
        }
        finally
        {
            InProgressReleases.TryRemove(releaseName, out _);
        }
    }


    /// <summary>
    /// Maps an IRC section to a FXP backend section based on triggers and rules.
    /// </summary>
    public static string GetMappedFxpBackendSection(
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
                    // to the x264 FXP backend section whenever a section had multiple tags.
                    var tags = section["tags"] as JArray;
                    if (tags != null && tags.Any())
                    {
                        string fallback = null;

                        foreach (var tag in tags)
                        {
                            string fxpBackendSection = ReadMappedFxpBackendSection(tag);
                            if (string.IsNullOrEmpty(fxpBackendSection))
                                continue;

                            string triggerRegex = tag["trigger_regex"]?.ToString();

                            if (string.IsNullOrEmpty(triggerRegex))
                            {
                                // tag without trigger = fallback if nothing matches
                                if (fallback == null)
                                    fallback = fxpBackendSection;
                                continue;
                            }

                            string regexPattern = triggerRegex.Trim();
                            bool isCaseInsensitive = false;

                            var delimited = Rx.DelimitedTrigger.Match(regexPattern);
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
                                        LogManager.Debug($"PreBot: Mapped IRC [{strippedSection}] → FXP backend [{fxpBackendSection}] (trigger '{regexPattern}')");
                                    }
                                    return fxpBackendSection;
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
                                LogManager.Debug($"PreBot: Mapped IRC [{strippedSection}] → FXP backend [{fallback}] (fallback tag)");
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
                    LogManager.Info($"No tags found, using IRC section [{strippedSection}] as FXP backend section");
                }
                return strippedSection;
            }

            string fallbackFxpBackendSection = null;

            // Per-call engine, loaded for THIS site's IRC section so the rule-based
            // tag disambiguation below evaluates against the correct (fresh) rules.
            var rulesEngine = new RulesEngine();
            rulesEngine.LoadRulesForIrcSection(siteConfig, strippedSection, strippedSection);

            // Parsed once — it only depends on the release name, not the tag.
            var parsedAttributes = ParseReleaseName(releaseName);

            // Process tags and triggers
            foreach (var tag in regularTags)
            {
                string triggerRegex = tag["trigger_regex"]?.ToString();
                string fxpBackendSection = ReadMappedFxpBackendSection(tag);

                if (string.IsNullOrEmpty(fxpBackendSection))
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

                    var delimited = Rx.DelimitedTrigger.Match(regexPattern);
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
                                LogManager.Debug($"Trigger '{regexPattern}' did NOT match, skipping [{fxpBackendSection}]");
                            }
                            continue;
                        }

                        if (EngineSettings.DebugEnabled)
                        {
                            LogManager.Success($"Trigger '{regexPattern}' matched! Using FXP backend section [{fxpBackendSection}]");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogManager.Error($"Invalid regex '{triggerRegex}': {ex.Message}");
                        continue;
                    }
                }

                // Strict match check
                if (string.Equals(fxpBackendSection, strippedSection, StringComparison.OrdinalIgnoreCase))
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Success($"Strict section match: [{strippedSection}] -> [{fxpBackendSection}]");
                    }
                    return fxpBackendSection;
                }

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

                if (parsedAttributes.TryGetValue("language", out var language))
                    input["language"] = language;

                if (parsedAttributes.TryGetValue("lang", out var lang))
                    input["lang"] = lang;

                var evaluationResult = rulesEngine.Evaluate(input, fxpBackendSection);

                if (string.Equals(evaluationResult, "ALLOW", StringComparison.OrdinalIgnoreCase))
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Success($"FXP backend section [{fxpBackendSection}] allowed by rules");
                    }
                    return fxpBackendSection;
                }

                if (!string.Equals(evaluationResult, "DROP", StringComparison.OrdinalIgnoreCase) && fallbackFxpBackendSection == null)
                {
                    fallbackFxpBackendSection = fxpBackendSection;
                }
            }

            // Use fallback if available
            //if (!string.IsNullOrEmpty(fallbackFxpBackendSection))
            //{
            //    if (EngineSettings.DebugEnabled)
            //    {
            //        LogManager.Success($"Using fallback FXP backend section [{fallbackFxpBackendSection}]");
            //    }
            //    return fallbackFxpBackendSection;
            //}

            if (EngineSettings.DebugEnabled)
            {
                LogManager.Warning($"No valid FXP backend section found for [{releaseName}]");
            }
            return null;
        }
        catch (Exception ex)
        {
            LogManager.Error($"Exception in GetMappedFxpBackendSection: {ex.Message}");
            return null;
        }
    }


    /// <summary>
    /// Extracts the group name from a release name.
    /// Format: Some.Release-GROUP
    /// </summary>
    /// <summary>
    /// True when the release group counts as one of the site's affils. Internal
    /// variants are matched automatically: group "GROUP_INT" matches affil "GROUP"
    /// and vice versa, so affils only need to be listed once (no _INT duplicates).
    /// </summary>
    public static bool GroupIsAffil(string group, IEnumerable<string> affils)
    {
        if (string.IsNullOrWhiteSpace(group) || affils == null)
            return false;

        string StripInt(string g) =>
            g.EndsWith("_INT", StringComparison.OrdinalIgnoreCase) ? g.Substring(0, g.Length - 4) : g;

        var groupBase = StripInt(group.Trim());

        foreach (var affil in affils)
        {
            if (string.IsNullOrWhiteSpace(affil))
                continue;

            var affilBase = StripInt(affil.Trim());
            if (string.Equals(groupBase, affilBase, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static string ExtractGroupFromRelease(string releaseName)
    {
        if (string.IsNullOrWhiteSpace(releaseName))
            return string.Empty;

        var match = Rx.Group.Match(releaseName);
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
    /// <summary>
    /// Runs a single release against ONE site section exactly the way a race does:
    /// mapping selection, section enabled, section skiplist, IMDB and TVMaze filters,
    /// then the section/mapping rules. Returns a step-by-step verdict for the Test
    /// release tools. Pretime and the cross-site blacklists are not evaluated here
    /// because they are not a property of the section.
    /// </summary>
    public static async Task<SectionTestResult> TestReleaseAgainstSection(
        JObject siteConfig, string ircSection, string releaseName)
    {
        var result = new SectionTestResult
        {
            Release = releaseName ?? "",
            Section = ircSection ?? "",
            Attributes = BuildRuleInput(releaseName ?? "")
        };
        result.Attributes["section"] = ircSection ?? "";

        void Step(string stage, bool passed, string detail) =>
            result.Steps.Add(new SectionTestStep { Stage = stage, Passed = passed, Detail = detail });

        try
        {
            if (siteConfig == null)
            {
                result.Allowed = false;
                result.Summary = "No site configuration.";
                return result;
            }

            var prefix = siteConfig["site_settings"]?["section_prefix"]?.ToString();
            var suffix = siteConfig["site_settings"]?["section_suffix"]?.ToString();

            // 1) Mapping: which FXP backend section this release routes to.
            var mapped = GetMappedFxpBackendSection(ircSection, releaseName, siteConfig, prefix, suffix);
            if (string.IsNullOrEmpty(mapped) || mapped.StartsWith("[ERROR]"))
            {
                Step("Mapping", false, $"section '{ircSection}' has no FXP backend mapping for this release");
                result.Allowed = false;
                result.Summary = "No FXP backend mapping, release would not race.";
                return result;
            }
            result.MappedFxpSection = mapped;
            Step("Mapping", true, $"routes to FXP backend section '{mapped}'");

            // 2) Section enabled for racing.
            if (!IsAllowedSection(ircSection, siteConfig))
            {
                Step("Section enabled", false, $"'{ircSection}' is not in this site's enabled race sections");
                result.Allowed = false;
                result.Summary = "Section is not enabled for racing.";
                return result;
            }
            Step("Section enabled", true, "section is enabled for racing");

            var configSection = siteConfig["sections"]?.FirstOrDefault(s =>
                string.Equals((string)s["irc_name"], ircSection, StringComparison.OrdinalIgnoreCase));

            // 3) Section release skiplist.
            var releaseSkiplists = configSection?["skiplists"]?.ToObject<List<string>>() ?? new List<string>();
            if (MatchesReleaseSkiplist(releaseName, releaseSkiplists, out var skipPattern))
            {
                Step("Skiplist", false, $"matched skiplist pattern '{skipPattern}'");
                result.Allowed = false;
                result.Summary = "Blocked by the section skiplist.";
                return result;
            }
            if (releaseSkiplists.Any())
                Step("Skiplist", true, "no skiplist pattern matched");

            // 3b) Affil-only section: only affil-group releases pass (download-only).
            if (configSection?["affil_only"]?.Value<bool>() == true)
            {
                var affilList = siteConfig["affils"]?.ToObject<List<string>>() ?? new List<string>();
                var grp = ExtractGroupFromRelease(releaseName);
                if (!GroupIsAffil(grp, affilList))
                {
                    Step("Affil only", false, $"section is affil-only and group '{(string.IsNullOrEmpty(grp) ? "unknown" : grp)}' is not an affil");
                    result.Allowed = false;
                    result.Summary = "Blocked: affil-only section and the group is not an affil.";
                    return result;
                }
                Step("Affil only", true, $"group '{grp}' is an affil, would be download-only");
            }

            // 4) IMDB filter (only when enabled on the section).
            if (configSection?["imdb"] is JObject imdb && imdb["enabled"]?.Value<bool>() == true)
            {
                var imdbBlock = await ValidateIMDB(releaseName, imdb, ircSection);
                if (imdbBlock != null)
                {
                    Step("IMDB", false, imdbBlock);
                    result.Allowed = false;
                    result.Summary = "Blocked by the IMDB filter.";
                    return result;
                }
                Step("IMDB", true, "passed the IMDB filter");
            }

            // 5) TVMaze filter (only when enabled on the section).
            if (configSection?["tvmaze"] is JObject tvmaze && tvmaze["enabled"]?.Value<bool>() == true)
            {
                var tvmazeBlock = await ValidateTVMaze(releaseName, tvmaze, ircSection);
                if (tvmazeBlock != null)
                {
                    Step("TVMaze", false, tvmazeBlock);
                    result.Allowed = false;
                    result.Summary = "Blocked by the TVMaze filter.";
                    return result;
                }
                Step("TVMaze", true, "passed the TVMaze filter");
            }

            // 6) Section + mapping rules.
            var rulesEngine = new RulesEngine();
            rulesEngine.LoadRulesForIrcSection(siteConfig, ircSection, mapped);
            var evaluation = rulesEngine.EvaluateDetailed(result.Attributes, mapped);
            result.DecidingRuleText = evaluation.DecidingRuleText;

            bool allowedByRules = string.Equals(evaluation.Decision, "ALLOW", StringComparison.OrdinalIgnoreCase);
            Step("Rules", allowedByRules,
                evaluation.DecidingRuleText == null
                    ? evaluation.Reason
                    : $"{evaluation.Reason}: {evaluation.DecidingRuleText}");

            if (!allowedByRules)
            {
                result.Allowed = false;
                result.Summary = "Dropped by the rules.";
                return result;
            }

            // 7) Global blacklist (Settings) and this site's own Blacklist tab.
            if (IsGloballyBlacklisted(releaseName, out var globalPattern))
            {
                Step("Blacklist", false, $"global blacklist pattern '{globalPattern}'");
                result.Allowed = false;
                result.Summary = "Blocked by the global blacklist.";
                return result;
            }

            var siteBlacklist = siteConfig["global_blacklist"]?.ToObject<List<string>>() ?? new List<string>();
            var hitBlacklist = siteBlacklist.FirstOrDefault(bl => MatchesBlacklistPattern(releaseName, bl));
            if (hitBlacklist != null)
            {
                Step("Blacklist", false, $"site blacklist pattern '{hitBlacklist}'");
                result.Allowed = false;
                result.Summary = "Blocked by this site's blacklist.";
                return result;
            }
            if (siteBlacklist.Any())
                Step("Blacklist", true, "no blacklist pattern matched");

            result.Allowed = true;
            result.Summary = $"Would race into FXP backend section '{mapped}'.";
            return result;
        }
        catch (Exception ex)
        {
            Step("Error", false, ex.Message);
            result.Allowed = false;
            result.Summary = $"Test failed: {ex.Message}";
            return result;
        }
    }

    /// <summary>
    /// Builds the rule-input dictionary for a release: the exact attribute set the
    /// rule engine sees during a real race (group, resolution, source, codec, range,
    /// repeat, internal, multi, year, language, lang, season, episode, type, release).
    /// The caller adds "section" for the section being evaluated. Shared by
    /// FilterAllowedSites and the Test release tools so they never drift apart.
    /// </summary>
    public static Dictionary<string, string> BuildRuleInput(string releaseName)
    {
        string codec, sourceType, resolution, range, group, repeatTag;
        bool isInternal, isMulti;

        if (TVMazeHelper.IsTVShow(releaseName))
        {
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
            { "release", releaseName }
        };

        if (parsedAttributes.TryGetValue("year", out var parsedYear)) input["year"] = parsedYear;
        if (parsedAttributes.TryGetValue("language", out var parsedLanguage)) input["language"] = parsedLanguage;
        if (parsedAttributes.TryGetValue("lang", out var parsedLang)) input["lang"] = parsedLang;
        if (parsedAttributes.TryGetValue("season", out var parsedSeason)) input["season"] = parsedSeason;
        if (parsedAttributes.TryGetValue("episode", out var parsedEpisode)) input["episode"] = parsedEpisode;
        if (parsedAttributes.TryGetValue("season_episode", out var parsedSeasonEpisode)) input["season_episode"] = parsedSeasonEpisode;
        if (parsedAttributes.TryGetValue("type", out var parsedType)) input["type"] = parsedType;

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

        return input;
    }

    public static Dictionary<string, string> ParseReleaseName(string releaseName)
    {
        var parsed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            // Year
            var yearMatch = Rx.Year.Match(releaseName);
            if (yearMatch.Success)
                parsed["year"] = yearMatch.Groups[1].Value;

            // Resolution (expanded)
            var resolutionMatch = Rx.Resolution.Match(releaseName);
            if (resolutionMatch.Success)
                parsed["resolution"] = resolutionMatch.Groups[1].Value.ToUpper();

            // Source (expanded)
            if (Rx.SourceBluray.IsMatch(releaseName))
            {
                parsed["source"] = "BLURAY";
            }
            else if (Rx.SourceUhdBluray.IsMatch(releaseName))
            {
                parsed["source"] = "UHD.BLURAY";
            }
            else
            {
                var sdMatch = Rx.SourceSd.Match(releaseName);
                if (sdMatch.Success)
                {
                    parsed["source"] = sdMatch.Groups[1].Value.ToUpper();
                }
                else
                {
                    var sourceMatch = Rx.SourceTv.Match(releaseName);
                    if (sourceMatch.Success)
                        parsed["source"] = sourceMatch.Groups[1].Value.ToUpper();
                }
            }

            // Codec
            var codecMatch = Rx.Codec.Match(releaseName);
            if (codecMatch.Success)
                parsed["codec"] = codecMatch.Groups[1].Value.ToUpper();

            // Range (HDR, DV, HLG). HDR.DV is normalized to DV.HDR like TRD.js does,
            // so rules only ever need to match one spelling.
            var rangeMatch = Rx.Range.Match(releaseName);
            if (rangeMatch.Success)
            {
                var rangeValue = rangeMatch.Groups[1].Value.ToUpper();
                parsed["range"] = rangeValue == "HDR.DV" ? "DV.HDR" : rangeValue;
            }

            // Group
            var groupMatch = Rx.Group.Match(releaseName);
            if (groupMatch.Success)
                parsed["group"] = groupMatch.Groups[1].Value;

            // Internal detection
            if (Rx.Internal.IsMatch(releaseName) ||
                (parsed.ContainsKey("group") && parsed["group"].ToUpper().EndsWith("_INT")))
            {
                parsed["internal"] = "true";
            }

            // Multi-language detection
            if (releaseName.ToUpper().Contains("MULTI"))
            {
                parsed["multi"] = "true";
            }

            // Language detection uses the editable Release Classifier language mapping.
            if (ReleaseClassifier.TryDetectLanguage(releaseName, out var detectedLanguage, out var detectedLangCode))
            {
                parsed["language"] = detectedLanguage.ToUpperInvariant();
                parsed["lang"] = detectedLangCode.ToUpperInvariant();
            }
            else
            {
                var languageMatch = Rx.Language.Match(releaseName);
                if (languageMatch.Success)
                {
                    parsed["language"] = languageMatch.Groups[1].Value.ToUpper();
                    parsed["lang"] = languageMatch.Groups[1].Value.ToUpper();
                }
            }

            // PROPER/REPACK/RERIP detection
            var repeatMatch = Rx.Repeat.Match(releaseName);
            if (repeatMatch.Success)
                parsed["repeat"] = repeatMatch.Groups[1].Value.ToUpper();

            // TV episode (multiple formats)
            // Format 1: S01E01-E02
            var episodeMatch1 = Rx.Episode1.Match(releaseName);
            if (episodeMatch1.Success)
            {
                parsed["season_episode"] = episodeMatch1.Groups[1].Value;
                parsed["episode"] = episodeMatch1.Groups[2].Value + episodeMatch1.Groups[3].Value;
                parsed["type"] = "tv";
            }
            else
            {
                // Format 2: S01E01 or Episode.01
                var episodeMatch2 = Rx.Episode2.Match(releaseName);
                if (episodeMatch2.Success)
                {
                    parsed["episode"] = episodeMatch2.Groups[2].Value;
                    parsed["type"] = "tv";
                }
                else
                {
                    // Format 3: 1x01
                    var episodeMatch3 = Rx.Episode3.Match(releaseName);
                    if (episodeMatch3.Success)
                    {
                        parsed["season"] = episodeMatch3.Groups[1].Value;
                        parsed["episode"] = episodeMatch3.Groups[2].Value;
                        parsed["type"] = "tv";
                    }
                    else
                    {
                        // Format 4: Date-based (2024.12.23)
                        var episodeMatch4 = Rx.Episode4.Match(releaseName);
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
                var seasonMatch = Rx.SeasonFallback.Match(releaseName);
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
    /// Gets detailed site information for a FXP backend section (for logging purposes).
    /// Uses cached data, no file I/O.
    /// </summary>
    public static (List<string> AllowedSites, List<string> SkippedSites) GetSiteDetailsForSection(string fxpBackendSection)
    {
        var allowedSites = new List<string>();
        var allSites = new List<string>();
        var fxpBackendServers = LoadFxpBackendIndex();

        lock (configLock)
        {
            foreach (var siteConfig in allSiteConfigs)
            {
                string siteName = GetSiteConfigKey(siteConfig);
                if (string.IsNullOrEmpty(siteName))
                    continue;

                allSites.Add(siteName);

                if (!SiteUsesEnabledFxpBackend(siteConfig, fxpBackendServers, out _))
                    continue;

                // Use cached lookup
                if (FxpBackendToIrcSectionCache.TryGetValue(siteName, out var cache) &&
                    cache.TryGetValue(fxpBackendSection, out string ircSection))
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
            // Bypass list: release names matching one of these patterns skip the
            // whole IMDB filter and are allowed through. Patterns support * and ?.
            var imdbBypass = GetStringList(config, "bypass_if_matches", "always_allow_if_matches");
            if (imdbBypass.Any(p => MatchesReleaseNamePattern(releaseName, p)))
            {
                LogManager.Info($"[{siteName}] [IMDB] ✔ Release matches bypass pattern, skipping IMDB filter");
                return null;
            }

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
            // Bypass list: if the release name matches one of these patterns the
            // whole TVMaze filter is skipped and the release is allowed through.
            // Handy for things like ".US." versions that TVMaze mis-matches to a
            // foreign show and would otherwise block. Patterns support * and ?.
            var tvmazeBypass = GetStringList(config, "bypass_if_matches", "always_allow_if_matches");
            if (tvmazeBypass.Any(p => MatchesReleaseNamePattern(releaseName, p)))
            {
                LogManager.Info($"[{siteName}] [TVMaze] ✔ Release matches bypass pattern, skipping TVMaze filter");
                return null;
            }

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

            // Series premiere year filter. Unlike the [year] rule (which reads the
            // year out of the release NAME and is unreliable for series), this uses
            // the actual premiere date TVMaze reports for the show.
            int minYear = config["min_year"]?.Value<int>() ?? 0;
            int maxYear = config["max_year"]?.Value<int>() ?? 0;
            if (minYear > 0 || maxYear > 0)
            {
                int premiereYear = 0;
                var premiered = show.Premiered ?? "";
                if (premiered.Length >= 4)
                {
                    int.TryParse(premiered.Substring(0, 4), out premiereYear);
                }

                if (premiereYear <= 0)
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ No premiere year available while a year filter is configured");
                    return "No premiere year available while a year filter is configured";
                }
                if (minYear > 0 && premiereYear < minYear)
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ Premiere year {premiereYear} < {minYear}");
                    return $"Premiere year {premiereYear} < {minYear}";
                }
                if (maxYear > 0 && premiereYear > maxYear)
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ Premiere year {premiereYear} > {maxYear}");
                    return $"Premiere year {premiereYear} > {maxYear}";
                }
            }

            // Same idea as the IMDB "English only" filter: TVMaze reports one
            // language per show, so a non-English show is blocked outright.
            if (config["only_english"]?.Value<bool>() == true)
            {
                var showLanguage = show.Language ?? "";
                if (!showLanguage.Equals("English", StringComparison.OrdinalIgnoreCase))
                {
                    var shown = string.IsNullOrWhiteSpace(showLanguage) ? "unknown" : showLanguage;
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ Not English ({shown})");
                    return $"Not English ({shown})";
                }
            }

            // Optional allow-list for setups that want more than English,
            // for example English plus Japanese for anime sections.
            var allowedLanguages = GetStringList(config, "allowed_languages", "languages");
            if (allowedLanguages.Any())
            {
                var showLanguage = show.Language ?? "";
                if (string.IsNullOrWhiteSpace(showLanguage))
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ No language data available while a language allow-list is configured");
                    return "No language data available while a language allow-list is configured";
                }

                if (!allowedLanguages.Contains(showLanguage, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"[{siteName}] [TVMaze] ❌ Language '{showLanguage}' not allowed");
                    return $"Language '{showLanguage}' not allowed";
                }
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
            FxpBackendToIrcSectionCache.Clear();
            InProgressReleases.Clear();
        }

        lock (blacklistLock)
        {
            globalBlacklist.Clear();
            globalBlacklistCompiled = new List<(string, Regex)>();
        }

        LogManager.Info("All caches cleared");
    }
}

/// <summary>
/// Represents a mapped tag with precompiled regex for performance.
/// </summary>
/// <summary>
/// One stage of a section release test (mapping, skiplist, IMDB, TVMaze, rules).
/// </summary>
public sealed class SectionTestStep
{
    public string Stage { get; set; }
    public bool Passed { get; set; }
    public string Detail { get; set; }
}

/// <summary>
/// Full result of testing one release against one site section.
/// </summary>
public sealed class SectionTestResult
{
    public string Release { get; set; }
    public string Section { get; set; }
    public string MappedFxpSection { get; set; }
    public bool Allowed { get; set; }
    public string Summary { get; set; }
    public string DecidingRuleText { get; set; }
    public Dictionary<string, string> Attributes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SectionTestStep> Steps { get; } = new();
}

public class MappedTag
{
    public string FxpBackendSection { get; set; }
    public Regex CompiledRegex { get; set; }
    public List<string> FxpBackendRules { get; set; }
}
