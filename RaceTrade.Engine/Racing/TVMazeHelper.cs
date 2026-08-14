using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaceTrade;

namespace RaceTrader
{
    /// <summary>
    /// Helper class for TVMaze API integration
    /// Provides TV show lookup and information retrieval
    /// </summary>
    public static class TVMazeHelper
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private const string API_BASE_URL = "https://api.tvmaze.com";

        /// <summary>
        /// Precompiled release-name regexes (same idea as TRD.js' EPISODE_FORM statics).
        /// The static Regex.* methods only cache ~15 patterns process-wide, so these
        /// inline patterns were being re-parsed on every announce.
        /// </summary>
        private static class Rx
        {
            private const RegexOptions CI = RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant;

            public static readonly Regex Codec = new(@"[\s._-]([xh]26[45]|xvid|VP[89])[\s._-]", CI);
            public static readonly Regex SourceBluray = new(@"[\s._-](((720p|1080p)\.(PURE\.)?M?BLURAY)|COMPLETE(\.PURE)?\.M?BLURAY)", CI);
            public static readonly Regex SourceUhdBluray = new(@"[\s._-]((2160p\.UHD\.M?BLURAY)|COMPLETE(\.UHD)?\.M?BLURAY)", CI);
            public static readonly Regex SourceSd = new(@"[\s._-](DVDRIP|BDRIP)", CI);
            public static readonly Regex SourceTv = new(@"[\s._-]([AU]?HDTV|AUHDTV|PDTV|DSR|WEBRIP|WEB)[\s._-]", CI);
            public static readonly Regex Resolution = new(@"[\s._-](720P|1080P|1280P|1440P|1920P|2160P|2300P|2700P|2880P)[\s._-]", CI);
            // HDR.DV added for parity with TRD.js (normalized to DV.HDR in ExtractRange).
            public static readonly Regex Range = new(@"[\s._-](DV\.HDR|HDR\.DV|HDR|DV|HLG)[\s._-]", CI);
            public static readonly Regex Internal = new(@"[\s._-](INTERNAL|INT)[\s._-]", CI);
            public static readonly Regex Multi = new(@"[\s._-]MULTI([\s._-]|$)", CI);
            public static readonly Regex Group = new(@"-([^-]+)$", RegexOptions.Compiled);
            public static readonly Regex GroupStrip = new(@"-[^-]+$", RegexOptions.Compiled);
            public static readonly Regex TvSxxExx = new(@"[\s._-]S\d+E\d+", CI);
            public static readonly Regex TvNxN = new(@"[\s._-]\d+x\d+[\s._-]", CI);
            public static readonly Regex TvEpisodeWord = new(@"[\s._-](?:Episode|E|Part)\.?\d+[\s._-]", CI);
            public static readonly Regex TvDate = new(@"[\s._-]\d{4}\.\d{2}\.\d{2}[\s._-]", RegexOptions.Compiled);
            public static readonly Regex EpisodeToken1 = new(@"[\s._-](S\d+E\d+-?E\d+)[\s._-]", CI);
            public static readonly Regex EpisodeToken2 = new(@"[\s._-]((?:S\d+)?(?:Episode|E|Part)\.?\d+)[\s._-]", CI);
            public static readonly Regex EpisodeToken3 = new(@"[\s._-](\d+x\d+)[\s._-]", CI);
            public static readonly Regex EpisodeToken4 = new(@"[\s._-](\d{4}\.\d{2}\.\d{2})[\s._-]", RegexOptions.Compiled);
            public static readonly Regex SeasonEpisode1 = new(@"[\s._-](S(\d+)E(\d+)-?E(\d+))[\s._-]", CI);
            public static readonly Regex SeasonEpisode2 = new(@"[\s._-](?:S(\d+)?)?(?:Episode|E|Part)\.?(\d+)[\s._-]", CI);
            public static readonly Regex SeasonEpisode3 = new(@"[\s._-](\d+)x(\d+)[\s._-]", CI);
            public static readonly Regex SeasonEpisode4 = new(@"[\s._-](\d{4})\.(\d{2}\.\d{2})[\s._-]", RegexOptions.Compiled);
        }

        // Rate limiting - TVMaze allows 20 requests per 10 seconds
        private static DateTime lastRequestTime = DateTime.MinValue;
        private static readonly TimeSpan minRequestInterval = TimeSpan.FromMilliseconds(500);

        static TVMazeHelper()
        {
            httpClient.DefaultRequestHeaders.Add("User-Agent", "RaceTrader/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(10);
        }

        #region Rate Limiting

        /// <summary>
        /// Ensures we don't exceed TVMaze rate limits
        /// </summary>
        private static readonly object rateLimitLock = new object();

        private static async Task RateLimitDelay()
        {
            // Releases are processed concurrently; compute and reserve the next slot
            // atomically, otherwise the interval check is defeated and we burst into
            // TVMaze's rate limiter (HTTP 429).
            TimeSpan delay;
            lock (rateLimitLock)
            {
                var now = DateTime.Now;
                var earliestNext = lastRequestTime + minRequestInterval;
                delay = earliestNext > now ? earliestNext - now : TimeSpan.Zero;

                // Reserve our slot up front so concurrent callers stagger.
                lastRequestTime = now + delay;
            }

            if (delay > TimeSpan.Zero)
                await Task.Delay(delay);
        }

        #endregion

        #region API Calls

        /// <summary>
        /// Lookup show by IMDB ID (with caching)
        /// </summary>
        /// <param name="imdbId">IMDB ID (e.g., "tt0944947")</param>
        /// <param name="cacheDays">Cache duration in days (default: 7)</param>
        /// <returns>Show information or null if not found</returns>
        public static async Task<TVMazeShow> LookupByImdb(string imdbId, int cacheDays = 7)
        {
            if (string.IsNullOrEmpty(imdbId))
                return null;

            // Ensure IMDB ID has correct format
            if (!imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                imdbId = "tt" + imdbId;
            }

            // Try cache first
            var cached = TVMazeCache.GetCachedShowByImdb(imdbId, cacheDays);
            if (cached != null)
            {
                return cached;
            }

            try
            {
                await RateLimitDelay();

                var url = $"{API_BASE_URL}/lookup/shows?imdb={imdbId}";
                var response = await httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var show = JsonConvert.DeserializeObject<TVMazeShow>(json);

                    // Cache the result
                    if (show != null)
                    {
                        TVMazeCache.CacheShow(show);
                    }

                    return show;
                }

                if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    LogManager.Warning($"Show not found on TVMaze for IMDB ID: {imdbId}");
                    return null;
                }

                LogManager.Error($"TVMaze API error: {response.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error looking up IMDB {imdbId} on TVMaze: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Lookup show by TheTVDB ID
        /// </summary>
        public static async Task<TVMazeShow> LookupByTvdb(int tvdbId)
        {
            try
            {
                await RateLimitDelay();

                var url = $"{API_BASE_URL}/lookup/shows?thetvdb={tvdbId}";
                var response = await httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<TVMazeShow>(json);
                }

                return null;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error looking up TVDB {tvdbId} on TVMaze: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Search for show by name (single best match) - with caching
        /// </summary>
        public static async Task<TVMazeShow> SearchShow(string showName, int cacheDays = 7)
        {
            if (string.IsNullOrEmpty(showName))
                return null;

            // Try cache first
            var cached = TVMazeCache.GetCachedShowByName(showName, cacheDays);
            if (cached != null)
            {
                return cached;
            }

            try
            {
                await RateLimitDelay();

                var url = $"{API_BASE_URL}/singlesearch/shows?q={Uri.EscapeDataString(showName)}";
                var response = await httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var show = JsonConvert.DeserializeObject<TVMazeShow>(json);

                    // Cache the result
                    if (show != null)
                    {
                        TVMazeCache.CacheShow(show);
                    }

                    return show;
                }

                return null;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error searching for show '{showName}' on TVMaze: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get episodes for a show
        /// </summary>
        public static async Task<List<TVMazeEpisode>> GetEpisodes(int showId)
        {
            try
            {
                await RateLimitDelay();

                var url = $"{API_BASE_URL}/shows/{showId}/episodes";
                var response = await httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<List<TVMazeEpisode>>(json);
                }

                return new List<TVMazeEpisode>();
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error getting episodes for show {showId}: {ex.Message}");
                return new List<TVMazeEpisode>();
            }
        }

        /// <summary>
        /// Get show with embedded episodes in one call
        /// </summary>
        public static async Task<TVMazeShow> GetShowWithEpisodes(int showId)
        {
            try
            {
                await RateLimitDelay();

                var url = $"{API_BASE_URL}/shows/{showId}?embed=episodes";
                var response = await httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonConvert.DeserializeObject<TVMazeShow>(json);
                }

                return null;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error getting show with episodes {showId}: {ex.Message}");
                return null;
            }
        }

        #endregion

        #region Additional Release Information Extraction

        /// <summary>
        /// Extract codec from release name (x264, x265, H264, H265, XviD, VP8, VP9)
        /// </summary>
        public static string ExtractCodec(string releaseName)
        {
            var match = Rx.Codec.Match(releaseName);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpper();
            }
            return null;
        }

        /// <summary>
        /// Extract source from release name (BluRay, WEB-DL, HDTV, etc.)
        /// </summary>
        public static string ExtractSource(string releaseName)
        {
            // Check for complete BluRay first
            if (Rx.SourceBluray.IsMatch(releaseName))
            {
                return "BLURAY";
            }

            if (Rx.SourceUhdBluray.IsMatch(releaseName))
            {
                return "UHD.BLURAY";
            }

            // Check SD sources
            var match = Rx.SourceSd.Match(releaseName);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpper();
            }

            // Check other sources
            match = Rx.SourceTv.Match(releaseName);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpper();
            }

            return null;
        }

        /// <summary>
        /// Extract resolution from release name (720P, 1080P, 2160P, etc.)
        /// </summary>
        public static string ExtractResolution(string releaseName)
        {
            var match = Rx.Resolution.Match(releaseName);
            if (match.Success)
            {
                return match.Groups[1].Value.ToUpper();
            }
            return null;
        }

        /// <summary>
        /// Extract HDR range from release name (HDR, DV, HLG, DV.HDR)
        /// </summary>
        public static string ExtractRange(string releaseName)
        {
            var match = Rx.Range.Match(releaseName);
            if (match.Success)
            {
                // HDR.DV -> DV.HDR normalization, same as TRD.js.
                var value = match.Groups[1].Value.ToUpper();
                return value == "HDR.DV" ? "DV.HDR" : value;
            }
            return null;
        }

        /// <summary>
        /// Check if release is INTERNAL
        /// </summary>
        public static bool IsInternal(string releaseName)
        {
            // Check for INTERNAL or INT tag
            if (Rx.Internal.IsMatch(releaseName))
            {
                return true;
            }

            // Check if group ends with _INT
            var group = ExtractGroup(releaseName);
            if (!string.IsNullOrEmpty(group) && group.ToUpper().EndsWith("_INT"))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Check if release is MULTI language
        /// </summary>
        public static bool IsMulti(string releaseName)
        {
            if (string.IsNullOrEmpty(releaseName))
                return false;

            // Must be a delimited MULTi tag, not any title containing the substring
            // (e.g. "Multitude") - same approach as IsInternal above.
            return Rx.Multi.IsMatch(releaseName);
        }

        /// <summary>
        /// Extract group from release name (the part after the last dash)
        /// </summary>
        public static string ExtractGroup(string releaseName)
        {
            var match = Rx.Group.Match(releaseName);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
            return null;
        }

        /// <summary>
        /// Extract repeat/dupe tags (PROPER, REPACK, RERIP, REAL.PROPER)
        /// </summary>
        public static string ExtractRepeatTag(string releaseName)
        {
            // Remove group first
            var nameWithoutGroup = Rx.GroupStrip.Replace(releaseName, "");

            var tags = new[] { "REAL.PROPER", "PROPER", "RERIP", "REPACK" };
            foreach (var tag in tags)
            {
                if (nameWithoutGroup.ToUpper().Contains(tag))
                {
                    return tag;
                }
            }
            return null;
        }

        #endregion

        #region Release Name Parsing

        /// <summary>
        /// Check if release name is a TV show (has episode information)
        /// </summary>
        public static bool IsTVShow(string releaseName)
        {
            if (string.IsNullOrEmpty(releaseName))
                return false;

            // Check for any episode pattern
            if (Rx.TvSxxExx.IsMatch(releaseName))
                return true;
            if (Rx.TvNxN.IsMatch(releaseName))
                return true;
            if (Rx.TvEpisodeWord.IsMatch(releaseName))
                return true;
            if (Rx.TvDate.IsMatch(releaseName))
                return true;

            return false;
        }

        /// <summary>
        /// Extract show name from release
        /// Takes everything before the episode token
        /// </summary>
        public static string ExtractShowNameFromRelease(string releaseName)
        {
            if (string.IsNullOrEmpty(releaseName))
                return null;

            // Remove file extension
            var name = System.IO.Path.GetFileNameWithoutExtension(releaseName);

            // Get the episode token
            var episodeToken = ExtractEpisodeToken(name);

            if (!string.IsNullOrEmpty(episodeToken))
            {
                // Split on the episode token and take everything before it
                var parts = Regex.Split(name, Regex.Escape(episodeToken) + @"[\s._-]", RegexOptions.IgnoreCase);
                if (parts.Length > 0)
                {
                    name = parts[0].TrimEnd('.', '_', '-', ' ');
                }
            }

            // Replace dots/underscores with spaces
            name = name.Replace('.', ' ').Replace('_', ' ');

            return name.Trim();
        }

        /// <summary>
        /// Extract the episode token from the release name (e.g., "S01E01", "1x01", etc.)
        /// </summary>
        public static string ExtractEpisodeToken(string releaseName)
        {
            if (string.IsNullOrEmpty(releaseName))
                return null;

            // Pattern 1: S01E01-E02
            var match = Rx.EpisodeToken1.Match(releaseName);
            if (match.Success)
                return match.Groups[1].Value;

            // Pattern 2: Episode01 or Part01 (with optional season)
            match = Rx.EpisodeToken2.Match(releaseName);
            if (match.Success)
                return match.Groups[1].Value;

            // Pattern 3: 1x01
            match = Rx.EpisodeToken3.Match(releaseName);
            if (match.Success)
                return match.Groups[1].Value;

            // Pattern 4: Date format 2024.01.15
            match = Rx.EpisodeToken4.Match(releaseName);
            if (match.Success)
                return match.Groups[1].Value;

            return null;
        }

        /// <summary>
        /// Parse season and episode from release name
        /// Supports multiple formats: S01E01, S01E01-E02, 1x01, Episode01, 2024.01.15
        /// </summary>
        public static (int? season, int? episode) ParseSeasonEpisode(string releaseName)
        {
            if (string.IsNullOrEmpty(releaseName))
                return (null, null);

            // Pattern 1: S01E01-E02 (multi-episode range)
            var match = Rx.SeasonEpisode1.Match(releaseName);
            if (match.Success)
            {
                var season = int.Parse(match.Groups[2].Value);
                var ep1 = match.Groups[3].Value;
                var ep2 = match.Groups[4].Value;

                // Handle E01-E02 -> return as "0102" if leading zero
                if (ep1[0] == '0')
                {
                    return (season, int.Parse(ep1 + ep2));
                }
                return (season, int.Parse(ep1 + ep2));
            }

            // Pattern 2: S01E01 or Episode01 or Part01
            match = Rx.SeasonEpisode2.Match(releaseName);
            if (match.Success)
            {
                int? season = null;
                if (!string.IsNullOrEmpty(match.Groups[1].Value))
                {
                    season = int.Parse(match.Groups[1].Value);
                }
                return (season, int.Parse(match.Groups[2].Value));
            }

            // Pattern 3: 1x01
            match = Rx.SeasonEpisode3.Match(releaseName);
            if (match.Success)
            {
                return (
                    int.Parse(match.Groups[1].Value),
                    int.Parse(match.Groups[2].Value)
                );
            }

            // Pattern 4: Date format 2024.01.15
            match = Rx.SeasonEpisode4.Match(releaseName);
            if (match.Success)
            {
                return (
                    int.Parse(match.Groups[1].Value), // Year as season
                    0 // Return episode as 0, will be stored as string "01.15"
                );
            }

            return (null, null);
        }

        #endregion

        #region Release Validation

        /// <summary>
        /// Enriches release information with TVMaze data
        /// </summary>
        public static async Task<TVShowReleaseInfo> EnrichReleaseInfo(string releaseName, int cacheDays = 7)
        {
            var releaseInfo = new TVShowReleaseInfo
            {
                ReleaseName = releaseName,
                IsValid = false
            };

            // Extract show name and search by name
            var showName = ExtractShowNameFromRelease(releaseName);
            if (!string.IsNullOrEmpty(showName))
            {
                releaseInfo.ShowName = showName;
                releaseInfo.Show = await SearchShow(showName, cacheDays);
            }

            // Parse episode info
            var (season, episode) = ParseSeasonEpisode(releaseName);
            releaseInfo.Season = season;
            releaseInfo.Episode = episode;

            // Extract release metadata
            releaseInfo.Codec = ExtractCodec(releaseName);
            releaseInfo.Source = ExtractSource(releaseName);
            releaseInfo.Resolution = ExtractResolution(releaseName);
            releaseInfo.Range = ExtractRange(releaseName);
            releaseInfo.Group = ExtractGroup(releaseName);
            releaseInfo.RepeatTag = ExtractRepeatTag(releaseName);
            releaseInfo.IsInternal = IsInternal(releaseName);
            releaseInfo.IsMulti = IsMulti(releaseName);

            // Validate
            releaseInfo.IsValid = releaseInfo.Show != null;

            return releaseInfo;
        }

        /// <summary>
        /// Check if release should be raced based on TVMaze data and release metadata
        /// </summary>
        public static bool ShouldRace(TVShowReleaseInfo releaseInfo, TVMazeConfig config = null)
        {
            if (config == null)
                config = new TVMazeConfig { FallbackOnError = true };

            // Check show data if available
            if (releaseInfo?.Show != null)
            {
                // Check if show is ended/cancelled
                if (config.SkipEndedShows && releaseInfo.Show.Status == "Ended")
                {
                    LogManager.Info($"❌ Show '{releaseInfo.Show.Name}' has ended");
                    return false;
                }

                // Check genres - allowed
                if (config.AllowedGenres != null && config.AllowedGenres.Any())
                {
                    if (releaseInfo.Show.Genres == null || !releaseInfo.Show.Genres.Any())
                    {
                        LogManager.Info("❌ No genre data available while genre allow-list is configured");
                        return false;
                    }
                    else
                    {
                        var hasAllowedGenre = releaseInfo.Show.Genres
                            .Any(g => config.AllowedGenres.Contains(g, StringComparer.OrdinalIgnoreCase));

                        if (!hasAllowedGenre)
                        {
                            LogManager.Info($"❌ Genre not in allowed list: {string.Join(", ", releaseInfo.Show.Genres)}");
                            return false;
                        }
                    }
                }

                // Check genres - blocked
                if (config.BlockedGenres != null && config.BlockedGenres.Any() && releaseInfo.Show.Genres != null)
                {
                    var hasBlockedGenre = releaseInfo.Show.Genres
                        .Any(g => config.BlockedGenres.Contains(g, StringComparer.OrdinalIgnoreCase));

                    if (hasBlockedGenre)
                    {
                        LogManager.Info($"❌ Genre is blocked: {string.Join(", ", releaseInfo.Show.Genres)}");
                        return false;
                    }
                }

                // Check rating
                if (config.MinRating > 0 && releaseInfo.Show.Rating?.Average != null)
                {
                    if (releaseInfo.Show.Rating.Average.Value < config.MinRating)
                    {
                        LogManager.Info($"❌ Rating {releaseInfo.Show.Rating.Average.Value} below minimum {config.MinRating}");
                        return false;
                    }
                }
                else if (config.MinRating > 0)
                {
                    LogManager.Info($"❌ No rating available while minimum {config.MinRating} is configured");
                    return false;
                }

                // Check networks
                if (config.AllowedNetworks != null && config.AllowedNetworks.Any())
                {
                    var network = releaseInfo.Show.Network?.Name ?? releaseInfo.Show.WebChannel?.Name;
                    if (string.IsNullOrEmpty(network) || !config.AllowedNetworks.Contains(network, StringComparer.OrdinalIgnoreCase))
                    {
                        LogManager.Info($"❌ Network '{network}' not in allowed list");
                        return false;
                    }
                }

                if (config.AllowedShowTypes != null && config.AllowedShowTypes.Any())
                {
                    var showType = releaseInfo.Show.Type ?? "";
                    if (string.IsNullOrWhiteSpace(showType))
                    {
                        LogManager.Info($"❌ Show type missing while allowed types are configured: {string.Join(", ", config.AllowedShowTypes)}");
                        return false;
                    }

                    if (!config.AllowedShowTypes.Contains(showType, StringComparer.OrdinalIgnoreCase))
                    {
                        LogManager.Info($"❌ Show type '{showType}' not in allowed list");
                        return false;
                    }
                }
            }

            // Check release metadata filters

            // Resolution
            if (config.AllowedResolutions != null && config.AllowedResolutions.Any())
            {
                if (string.IsNullOrEmpty(releaseInfo.Resolution) ||
                    !config.AllowedResolutions.Contains(releaseInfo.Resolution, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Info($"❌ Resolution '{releaseInfo.Resolution}' not in allowed list");
                    return false;
                }
            }
            if (config.BlockedResolutions != null && config.BlockedResolutions.Any())
            {
                if (!string.IsNullOrEmpty(releaseInfo.Resolution) &&
                    config.BlockedResolutions.Contains(releaseInfo.Resolution, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Info($"❌ Resolution '{releaseInfo.Resolution}' is blocked");
                    return false;
                }
            }

            // Source
            if (config.AllowedSources != null && config.AllowedSources.Any())
            {
                if (string.IsNullOrEmpty(releaseInfo.Source) ||
                    !config.AllowedSources.Contains(releaseInfo.Source, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Info($"❌ Source '{releaseInfo.Source}' not in allowed list");
                    return false;
                }
            }
            if (config.BlockedSources != null && config.BlockedSources.Any())
            {
                if (!string.IsNullOrEmpty(releaseInfo.Source) &&
                    config.BlockedSources.Contains(releaseInfo.Source, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Info($"❌ Source '{releaseInfo.Source}' is blocked");
                    return false;
                }
            }

            // Codec
            if (config.AllowedCodecs != null && config.AllowedCodecs.Any())
            {
                if (string.IsNullOrEmpty(releaseInfo.Codec) ||
                    !config.AllowedCodecs.Contains(releaseInfo.Codec, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Info($"❌ Codec '{releaseInfo.Codec}' not in allowed list");
                    return false;
                }
            }
            if (config.BlockedCodecs != null && config.BlockedCodecs.Any())
            {
                if (!string.IsNullOrEmpty(releaseInfo.Codec) &&
                    config.BlockedCodecs.Contains(releaseInfo.Codec, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Info($"❌ Codec '{releaseInfo.Codec}' is blocked");
                    return false;
                }
            }

            // Group
            if (config.AllowedGroups != null && config.AllowedGroups.Any())
            {
                if (string.IsNullOrEmpty(releaseInfo.Group) ||
                    !config.AllowedGroups.Contains(releaseInfo.Group, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Info($"❌ Group '{releaseInfo.Group}' not in allowed list");
                    return false;
                }
            }
            if (config.BlockedGroups != null && config.BlockedGroups.Any())
            {
                if (!string.IsNullOrEmpty(releaseInfo.Group) &&
                    config.BlockedGroups.Contains(releaseInfo.Group, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Info($"❌ Group '{releaseInfo.Group}' is blocked");
                    return false;
                }
            }

            // Internal
            if (config.SkipInternals && releaseInfo.IsInternal)
            {
                LogManager.Info("❌ Internal release (skipping internals)");
                return false;
            }
            if (config.OnlyInternals && !releaseInfo.IsInternal)
            {
                LogManager.Info("❌ Not internal (only racing internals)");
                return false;
            }

            // HDR
            if (config.SkipHDR && !string.IsNullOrEmpty(releaseInfo.Range))
            {
                LogManager.Info($"❌ HDR/DV release '{releaseInfo.Range}' (skipping HDR)");
                return false;
            }
            if (config.OnlyHDR && string.IsNullOrEmpty(releaseInfo.Range))
            {
                LogManager.Info("❌ Not HDR (only racing HDR)");
                return false;
            }

            // Propers/Repacks
            if (config.SkipPropers && !string.IsNullOrEmpty(releaseInfo.RepeatTag))
            {
                LogManager.Info($"❌ {releaseInfo.RepeatTag} release (skipping propers)");
                return false;
            }

            return true;
        }

        #endregion
    }

    #region Data Models

    public class TVMazeShow
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("language")]
        public string Language { get; set; }

        [JsonProperty("genres")]
        public List<string> Genres { get; set; }

        [JsonProperty("status")]
        public string Status { get; set; }

        [JsonProperty("runtime")]
        public int? Runtime { get; set; }

        [JsonProperty("premiered")]
        public string Premiered { get; set; }

        [JsonProperty("officialSite")]
        public string OfficialSite { get; set; }

        [JsonProperty("rating")]
        public TVMazeRating Rating { get; set; }

        [JsonProperty("network")]
        public TVMazeNetwork Network { get; set; }

        [JsonProperty("webChannel")]
        public TVMazeWebChannel WebChannel { get; set; }

        [JsonProperty("externals")]
        public TVMazeExternals Externals { get; set; }

        [JsonProperty("image")]
        public TVMazeImage Image { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }

        [JsonProperty("_embedded")]
        public TVMazeEmbedded Embedded { get; set; }
    }

    public class TVMazeRating
    {
        [JsonProperty("average")]
        public double? Average { get; set; }
    }

    public class TVMazeNetwork
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("country")]
        public TVMazeCountry Country { get; set; }
    }

    public class TVMazeWebChannel
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("country")]
        public TVMazeCountry Country { get; set; }
    }

    public class TVMazeCountry
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("code")]
        public string Code { get; set; }

        [JsonProperty("timezone")]
        public string Timezone { get; set; }
    }

    public class TVMazeExternals
    {
        [JsonProperty("tvrage")]
        public int? TvRage { get; set; }

        [JsonProperty("thetvdb")]
        public int? TheTvDb { get; set; }

        [JsonProperty("imdb")]
        public string Imdb { get; set; }
    }

    public class TVMazeImage
    {
        [JsonProperty("medium")]
        public string Medium { get; set; }

        [JsonProperty("original")]
        public string Original { get; set; }
    }

    public class TVMazeEpisode
    {
        [JsonProperty("id")]
        public int Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("season")]
        public int Season { get; set; }

        [JsonProperty("number")]
        public int? Number { get; set; }

        [JsonProperty("airdate")]
        public string AirDate { get; set; }

        [JsonProperty("airtime")]
        public string AirTime { get; set; }

        [JsonProperty("airstamp")]
        public DateTime? AirStamp { get; set; }

        [JsonProperty("runtime")]
        public int? Runtime { get; set; }

        [JsonProperty("summary")]
        public string Summary { get; set; }
    }

    public class TVMazeEmbedded
    {
        [JsonProperty("episodes")]
        public List<TVMazeEpisode> Episodes { get; set; }
    }

    public class TVShowReleaseInfo
    {
        public string ReleaseName { get; set; }
        public string ImdbId { get; set; }
        public string ShowName { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public TVMazeShow Show { get; set; }
        public bool IsValid { get; set; }

        // Release metadata
        public string Codec { get; set; }
        public string Source { get; set; }
        public string Resolution { get; set; }
        public string Range { get; set; }
        public string Group { get; set; }
        public string RepeatTag { get; set; }
        public bool IsInternal { get; set; }
        public bool IsMulti { get; set; }
    }

    #endregion

    #region Config Class

    public class TVMazeConfig
    {
        public bool Enabled { get; set; }
        public bool SkipEndedShows { get; set; }
        public List<string> AllowedGenres { get; set; }
        public List<string> BlockedGenres { get; set; }
        public double MinRating { get; set; }
        public List<string> AllowedNetworks { get; set; }
        public List<string> AllowedShowTypes { get; set; }
        public int CacheDurationDays { get; set; }
        public bool FallbackOnError { get; set; }

        // Release metadata filters
        public List<string> AllowedResolutions { get; set; }  // e.g., ["1080P", "2160P"]
        public List<string> BlockedResolutions { get; set; }
        public List<string> AllowedSources { get; set; }      // e.g., ["WEB", "BLURAY"]
        public List<string> BlockedSources { get; set; }
        public List<string> AllowedCodecs { get; set; }       // e.g., ["X265", "H265"]
        public List<string> BlockedCodecs { get; set; }
        public List<string> AllowedGroups { get; set; }       // e.g., ["KILLERS", "ROVERS"]
        public List<string> BlockedGroups { get; set; }
        public bool SkipInternals { get; set; }
        public bool OnlyInternals { get; set; }
        public bool SkipHDR { get; set; }
        public bool OnlyHDR { get; set; }
        public bool SkipPropers { get; set; }                 // Skip PROPER/REPACK/RERIP
    }

    #endregion
}
