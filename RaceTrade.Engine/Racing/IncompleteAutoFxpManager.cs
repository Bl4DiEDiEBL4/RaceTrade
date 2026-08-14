using RaceTrade;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace RaceTrader
{
    public sealed class IncompleteReleaseEntry
    {
        public string Site { get; set; }
        public string Channel { get; set; }
        public string Section { get; set; }
        public string Release { get; set; }
        public string RawLine { get; set; }
    }

    public static class IncompleteAutoFxpManager
    {
        public const string DefaultMarkerRegex = @"WARN:\s+AUTONUKE\s+INCOMPLETE";
        public const string DefaultSectionRegex = @"INCOMPLETE\s+\[([^\]]+)\]";
        public const string DefaultReleaseRegex = @"INCOMPLETE\s+\[[^\]]+\]\s+(\S+)";
        public const string DefaultSearchCommandTemplate = "SITE SEARCH {release}";
        public const string DefaultDstPathTemplate = "/{section}";

        private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
        private static readonly TimeSpan DuplicateWindow = TimeSpan.FromMinutes(30);
        private static readonly ConcurrentDictionary<string, DateTime> RecentAttempts =
            new(StringComparer.OrdinalIgnoreCase);

        public static bool IsIncompleteLine(SiteConfig site, string line)
        {
            if (site?.SiteSettings == null || string.IsNullOrWhiteSpace(line))
                return false;

            var pattern = Effective(site.SiteSettings.IncompleteMarkerRegex, DefaultMarkerRegex);
            return SafeIsMatch(line, pattern);
        }

        public static async Task TryRepairFromLineAsync(
            SiteConfig targetSite,
            string line,
            string channelName,
            CancellationToken token)
        {
            try
            {
                if (targetSite?.SiteSettings == null)
                    return;

                var targetName = targetSite.SiteSettings.Sitename ?? "";

                if (!targetSite.SiteSettings.IncompleteAutoFxpEnabled)
                {
                    LogManager.LogCBFTP(
                        CBFTPEventType.Info,
                        $"[IncompleteAutoFXP] Incomplete warning detected on '{targetName}', but auto FXP is disabled.",
                        releaseName: null,
                        targetSite: targetName);
                    return;
                }

                if (!TryParseIncomplete(targetSite, line, channelName, out var entry, out var reason))
                {
                    LogManager.LogCBFTP(
                        CBFTPEventType.Error,
                        $"[IncompleteAutoFXP] Could not parse incomplete warning on '{targetName}': {reason}",
                        releaseName: null,
                        targetSite: targetName);
                    return;
                }

                if (!MarkAttempt(entry))
                {
                    LogManager.LogCBFTP(
                        CBFTPEventType.Info,
                        $"[IncompleteAutoFXP] Repair already attempted recently for [{entry.Section}] {entry.Release} on '{targetName}', skipping duplicate warning.",
                        releaseName: entry.Release,
                        targetSite: targetName);
                    return;
                }

                await TryRepairAsync(targetSite, entry, token);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (Exception ex)
            {
                var siteName = targetSite?.SiteSettings?.Sitename;
                LogManager.LogCBFTP(
                    CBFTPEventType.Error,
                    $"[IncompleteAutoFXP] Unexpected repair error: {ex.Message}",
                    releaseName: null,
                    targetSite: siteName);
            }
        }

        public static bool TryParseIncomplete(
            SiteConfig site,
            string line,
            string channelName,
            out IncompleteReleaseEntry entry,
            out string reason)
        {
            entry = null;
            reason = "";

            var settings = site?.SiteSettings;
            if (settings == null)
            {
                reason = "site settings are missing";
                return false;
            }

            var ignored = FirstIgnoredWord(line, settings.IncompleteIgnoreWords);
            if (!string.IsNullOrEmpty(ignored))
            {
                reason = $"ignored word matched: {ignored}";
                return false;
            }

            var marker = Effective(settings.IncompleteMarkerRegex, DefaultMarkerRegex);
            var sectionRegex = Effective(settings.IncompleteSectionRegex, DefaultSectionRegex);
            var releaseRegex = Effective(settings.IncompleteReleaseRegex, DefaultReleaseRegex);

            try
            {
                if (!Regex.IsMatch(line ?? "", marker, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout))
                {
                    reason = "marker did not match";
                    return false;
                }

                var sectionMatch = Regex.Match(line ?? "", sectionRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                if (!sectionMatch.Success || sectionMatch.Groups.Count < 2 || string.IsNullOrWhiteSpace(sectionMatch.Groups[1].Value))
                {
                    reason = "section regex did not capture group 1";
                    return false;
                }

                var releaseMatch = Regex.Match(line ?? "", releaseRegex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
                if (!releaseMatch.Success || releaseMatch.Groups.Count < 2 || string.IsNullOrWhiteSpace(releaseMatch.Groups[1].Value))
                {
                    reason = "release regex did not capture group 1";
                    return false;
                }

                var section = TrimPrefixSuffix(
                    sectionMatch.Groups[1].Value.Trim(),
                    settings.IncompleteSectionPrefix,
                    settings.IncompleteSectionSuffix);

                var release = releaseMatch.Groups[1].Value.Trim();

                if (string.IsNullOrWhiteSpace(section))
                {
                    reason = "section is empty after prefix/suffix cleanup";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(release))
                {
                    reason = "release is empty";
                    return false;
                }

                entry = new IncompleteReleaseEntry
                {
                    Site = settings.Sitename,
                    Channel = channelName,
                    Section = section,
                    Release = release,
                    RawLine = line
                };

                return true;
            }
            catch (ArgumentException ex)
            {
                reason = $"invalid regex: {ex.Message}";
                return false;
            }
            catch (RegexMatchTimeoutException)
            {
                reason = "regex timed out";
                return false;
            }
        }

        private static async Task TryRepairAsync(
            SiteConfig targetSite,
            IncompleteReleaseEntry entry,
            CancellationToken token)
        {
            var targetName = targetSite.SiteSettings.Sitename;
            var sourceSites = LoadAllSiteConfigs()
                .Where(s => s?.SiteSettings != null)
                .Where(s => s.SiteSettings.IncompleteSearchSource)
                .Where(s => !s.SiteSettings.DisableSite)
                .Where(s => !string.Equals(s.SiteSettings.Sitename, targetName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            LogManager.LogCBFTP(
                CBFTPEventType.Info,
                $"[IncompleteAutoFXP] Detected incomplete [{entry.Section}] {entry.Release} on '{targetName}'. Searching {sourceSites.Count} source site(s).",
                releaseName: entry.Release,
                targetSite: targetName);

            if (sourceSites.Count == 0)
            {
                LogManager.LogCBFTP(
                    CBFTPEventType.Info,
                    $"[IncompleteAutoFXP] No sites are marked as incomplete search sources for '{entry.Release}'.",
                    releaseName: entry.Release,
                    targetSite: targetName);
                return;
            }

            var searchName = NormalizeReleaseNameForSearch(entry.Release);
            foreach (var sourceSite in sourceSites)
            {
                if (token.IsCancellationRequested)
                    break;

                var sourceName = sourceSite.SiteSettings.Sitename;
                var searchEntry = new IncompleteReleaseEntry
                {
                    Site = entry.Site,
                    Channel = entry.Channel,
                    Section = entry.Section,
                    Release = searchName,
                    RawLine = entry.RawLine
                };

                var searchCommand = ApplyTemplate(
                    Effective(sourceSite.SiteSettings.IncompleteSearchCommandTemplate, DefaultSearchCommandTemplate),
                    searchEntry,
                    targetName,
                    sourceName);

                LogManager.LogCBFTP(
                    CBFTPEventType.Info,
                    $"[IncompleteAutoFXP] Searching '{sourceName}' for '{searchName}' with '{searchCommand}'.",
                    releaseName: entry.Release,
                    targetSite: sourceName);

                var rawSearch = await CbftpRequestHelper.RunRawAsync(searchCommand, sourceName, token);
                if (string.IsNullOrWhiteSpace(rawSearch))
                {
                    LogManager.LogCBFTP(
                        CBFTPEventType.Info,
                        $"[IncompleteAutoFXP] No SITE SEARCH result for '{searchName}' on '{sourceName}'.",
                        releaseName: entry.Release,
                        targetSite: sourceName);
                    continue;
                }

                var srcPath = ExtractReleasePathFromSearch(rawSearch, searchName);
                if (string.IsNullOrWhiteSpace(srcPath))
                {
                    LogManager.LogCBFTP(
                        CBFTPEventType.Info,
                        $"[IncompleteAutoFXP] Could not extract source path for '{searchName}' on '{sourceName}'.",
                        releaseName: entry.Release,
                        targetSite: sourceName);
                    continue;
                }

                var dstPath = ApplyTemplate(
                    Effective(targetSite.SiteSettings.IncompleteDstPathTemplate, DefaultDstPathTemplate),
                    entry,
                    targetName,
                    sourceName);

                LogManager.LogCBFTP(
                    CBFTPEventType.Info,
                    $"[IncompleteAutoFXP] Found '{searchName}' on '{sourceName}' at '{srcPath}'. Queueing FXP to '{targetName}:{dstPath}'.",
                    releaseName: entry.Release,
                    targetSite: targetName);

                var ok = await CbftpRacer.StartRequestTransferJob(
                    sourceName,
                    targetName,
                    dstPath,
                    entry.Release,
                    srcPath,
                    srcIsSection: false);

                if (ok)
                {
                    LogManager.LogCBFTP(
                        CBFTPEventType.SpreadJobSent,
                        $"[IncompleteAutoFXP] Queued repair FXP for [{entry.Section}] {entry.Release}: {sourceName} -> {targetName}",
                        releaseName: entry.Release,
                        targetSite: targetName);
                    return;
                }

                LogManager.LogCBFTP(
                    CBFTPEventType.SpreadJobFailed,
                    $"[IncompleteAutoFXP] Could not queue repair FXP from '{sourceName}' to '{targetName}'. Trying next source.",
                    releaseName: entry.Release,
                    targetSite: targetName);
            }

            LogManager.LogCBFTP(
                CBFTPEventType.SpreadJobFailed,
                $"[IncompleteAutoFXP] No usable source found for [{entry.Section}] {entry.Release}.",
                releaseName: entry.Release,
                targetSite: targetName);
        }

        private static List<SiteConfig> LoadAllSiteConfigs()
        {
            if (!Directory.Exists("sites"))
                return new List<SiteConfig>();

            var list = new List<SiteConfig>();
            foreach (var path in Directory.GetFiles("sites", "*.json"))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if (IsReservedSiteName(name))
                    continue;

                if (SiteConfigManager.TryGetSiteConfig(name, out var cfg) && cfg != null)
                    list.Add(cfg);
            }

            return list;
        }

        private static bool MarkAttempt(IncompleteReleaseEntry entry)
        {
            CleanupAttempts();

            var key = $"{entry.Site}|{entry.Release}";
            var now = DateTime.UtcNow;
            if (RecentAttempts.TryGetValue(key, out var last) && now - last < DuplicateWindow)
                return false;

            RecentAttempts[key] = now;
            return true;
        }

        private static void CleanupAttempts()
        {
            var cutoff = DateTime.UtcNow - TimeSpan.FromHours(1);
            foreach (var kv in RecentAttempts.ToArray())
            {
                if (kv.Value < cutoff)
                    RecentAttempts.TryRemove(kv.Key, out _);
            }
        }

        private static string ExtractReleasePathFromSearch(string rawSearch, string releaseName)
        {
            if (string.IsNullOrWhiteSpace(rawSearch) || string.IsNullOrWhiteSpace(releaseName))
                return null;

            var lines = rawSearch.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawLine in lines)
            {
                var line = rawLine.Trim();
                if (!line.Contains(releaseName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var idxSlash = line.IndexOf('/');
                if (idxSlash < 0)
                    continue;

                var pathPart = line[idxSlash..].Trim();
                var nameIdx = pathPart.IndexOf(releaseName, StringComparison.OrdinalIgnoreCase);
                if (nameIdx < 0)
                    continue;

                var slashBeforeName = pathPart.LastIndexOf('/', nameIdx);
                if (slashBeforeName <= 0)
                    continue;

                return pathPart[..slashBeforeName];
            }

            return null;
        }

        private static string ApplyTemplate(
            string template,
            IncompleteReleaseEntry entry,
            string targetSite,
            string sourceSite)
        {
            var result = template ?? "";
            result = result.Replace("{release}", entry.Release ?? "", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("{section}", entry.Section ?? "", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("{sitename}", targetSite ?? "", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("{site}", targetSite ?? "", StringComparison.OrdinalIgnoreCase);
            result = result.Replace("{source}", sourceSite ?? "", StringComparison.OrdinalIgnoreCase);
            return result.Trim();
        }

        private static string NormalizeReleaseNameForSearch(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;

            char[] dashLikes =
            {
                '\u2010',
                '\u2011',
                '\u2012',
                '\u2013',
                '\u2014',
                '\u2212'
            };

            var chars = name.ToCharArray();
            for (var i = 0; i < chars.Length; i++)
            {
                if (dashLikes.Contains(chars[i]))
                    chars[i] = '-';
            }

            return new string(chars);
        }

        private static string FirstIgnoredWord(string input, string ignoreWordsText) =>
            (ignoreWordsText ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(word => input.Contains(word, StringComparison.OrdinalIgnoreCase)) ?? "";

        private static string TrimPrefixSuffix(string value, string prefix, string suffix)
        {
            var result = value ?? "";
            if (!string.IsNullOrEmpty(prefix) && result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                result = result[prefix.Length..];

            if (!string.IsNullOrEmpty(suffix) && result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                result = result[..^suffix.Length];

            return result.Trim();
        }

        private static string Effective(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private static bool SafeIsMatch(string input, string pattern)
        {
            try
            {
                return Regex.IsMatch(input ?? "", pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsReservedSiteName(string name) =>
            string.IsNullOrWhiteSpace(name) ||
            string.Equals(name, "new_site", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "template", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "example", StringComparison.OrdinalIgnoreCase);
    }
}
