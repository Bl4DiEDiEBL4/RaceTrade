using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaceTrade;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Globalization;

namespace RaceTrader
{
    /// <summary>
    /// Helper class for IMDB API integration.
    /// Provides movie lookup and information retrieval
    /// </summary>
    public static class IMDBHelper
    {
        private static readonly HttpClient httpClient = new HttpClient();
        private const string TIFFARA_API_BASE = "https://api.tiffara.com";
        private const string TMDB_API_BASE = "https://api.themoviedb.org/3";
        private const string SETTINGS_FILE = "settings/settings.json";

        /// <summary>
        /// Precompiled release-name regexes (same idea as TRD.js' precompiled statics).
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
            public static readonly Regex TitleBeforeYear = new(@"^(.+?)[\s._-](19\d{2}|20\d{2})[\s._-]", RegexOptions.Compiled);
            public static readonly Regex Year = new(@"\b(19\d{2}|20\d{2})\b", RegexOptions.Compiled);
        }
        public const string ProviderTiffara = "tiffara";
        public const string ProviderTmdb = "tmdb";

        private static DateTime lastRequestTime = DateTime.MinValue;
        private static readonly TimeSpan minRequestInterval = TimeSpan.FromMilliseconds(500);

        static IMDBHelper()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            httpClient.DefaultRequestHeaders.Add("User-Agent", "RaceTrader/1.0");
            httpClient.Timeout = TimeSpan.FromSeconds(15);
        }

        #region Rate Limiting

        private static readonly object rateLimitLock = new object();

        private static async Task RateLimitDelay()
        {
            // Releases are processed concurrently; compute and reserve the next slot
            // atomically, otherwise the interval check is defeated and we burst into
            // the API's rate limiter (HTTP 429).
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

        private sealed class ImdbApiProvider
        {
            public string Key { get; set; }
            public string Name { get; set; }
            public string BaseUrl { get; set; }
        }

        private static readonly ImdbApiProvider TiffaraProvider = new ImdbApiProvider
        {
            Key = ProviderTiffara,
            Name = "Tiffara",
            BaseUrl = TIFFARA_API_BASE
        };

        /// <summary>
        /// Lookup movie by IMDB ID (with caching)
        /// </summary>
        public static async Task<IMDBMovie> LookupByImdb(string imdbId, int cacheDays = 30)
        {
            if (string.IsNullOrEmpty(imdbId))
                return null;

            // Ensure IMDB ID has correct format
            if (!imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase))
            {
                imdbId = "tt" + imdbId;
            }

            var selectedProvider = GetSelectedMovieProvider();

            // Try cache first
            var cached = selectedProvider == ProviderTiffara && cacheDays > 0
                ? IMDBCache.GetCachedMovieByImdb(imdbId, cacheDays)
                : null;
            if (cached != null)
            {
                return cached;
            }

            return selectedProvider == ProviderTmdb
                ? await LookupByImdbViaTmdb(imdbId)
                : await LookupByImdbFromProvider(imdbId, TiffaraProvider);
        }

        private static async Task<IMDBMovie> LookupByImdbFromProvider(string imdbId, ImdbApiProvider provider)
        {
            try
            {
                await RateLimitDelay();

                // IMDb-compatible endpoint: GET /titles/{titleId}
                var url = $"{provider.BaseUrl}/titles/{imdbId}";
                var response = await httpClient.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var result = JObject.Parse(json);

                    // Check if movie was found
                    if (result["id"] != null)
                    {
                        var movie = ParseImdbApiResponse(result);

                        // Cache the result
                        if (movie != null)
                        {
                            movie.DataSource = provider.Name;
                            IMDBCache.CacheMovie(movie);
                        }

                        return movie;
                    }

                    LogManager.Warning($"Movie not found on {provider.Name}: {imdbId}");
                    IMDBCache.CacheNotFound(imdbId);
                    return null;
                }

                LogManager.Error($"{provider.Name} API error: {response.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error looking up IMDb {imdbId} via {provider.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Search for movie by title (with caching)
        /// </summary>
        public static async Task<IMDBMovie> SearchMovie(string title, int? year = null, int cacheDays = 30)
        {
            var result = await SearchMovieDetailed(title, year, cacheDays);
            return result.Movie;
        }

        public static async Task<MovieSearchResult> SearchMovieDetailed(string title, int? year = null, int cacheDays = 30)
        {
            if (string.IsNullOrEmpty(title))
                return MovieSearchResult.Failed(title, year, "Empty title");

            var searchResult = new MovieSearchResult
            {
                RequestedTitle = title,
                RequestedYear = year
            };
            var searchKey = year.HasValue ? $"{title}|{year}" : title;
            var selectedProvider = GetSelectedMovieProvider();

            if (selectedProvider == ProviderTmdb)
            {
                var tmdbOnlyMovie = await SearchMovieViaTmdb(title, year, cacheDays, searchResult, false);
                if (tmdbOnlyMovie != null)
                {
                    searchResult.Movie = tmdbOnlyMovie;
                    if (string.IsNullOrWhiteSpace(searchResult.Source))
                        searchResult.Source = tmdbOnlyMovie.DataSource ?? "TMDb";
                    return searchResult;
                }

                IMDBCache.CacheNotFound(searchKey);
                if (string.IsNullOrWhiteSpace(searchResult.Message))
                    searchResult.Message = $"No TMDb data found for {title} ({year})";
                return searchResult;
            }

            // Try cache first
            var cached = cacheDays > 0 ? IMDBCache.GetCachedMovieByTitle(title, year, cacheDays) : null;
            if (cached != null)
            {
                searchResult.Movie = cached;
                searchResult.Source = "IMDb cache";
                searchResult.UsedCache = true;
                searchResult.Message = $"Cache hit for {title} ({year})";
                return searchResult;
            }

            var provider = TiffaraProvider;
            foreach (var query in BuildSearchQueries(title, year))
            {
                try
                {
                    await RateLimitDelay();

                    // IMDb-compatible search endpoint:
                    // GET /search/titles?query={query}&limit={limit}
                    var url = $"{provider.BaseUrl}/search/titles?query={Uri.EscapeDataString(query)}&limit=20";
                    var response = await httpClient.GetAsync(url);

                    if (!response.IsSuccessStatusCode)
                    {
                        searchResult.ImdbApiFailed = true;
                        searchResult.AddMessage($"{provider.Name} search '{query}' failed: {response.StatusCode}");
                        LogManager.Warning($"{provider.Name} search for '{query}' failed: {response.StatusCode}");
                        continue;
                    }

                    var json = await response.Content.ReadAsStringAsync();
                    var result = JObject.Parse(json);
                    var results = ExtractSearchResults(result);
                    if (results != null && results.Any())
                    {
                        var bestMatch = PickBestMovieMatch(results, title, year);

                        // Search hits are SPARSE (no languages/countries/directors) -
                        // caching them as full records made only_english/only_us_country
                        // filters block everything for 30 days. Fetch the full record.
                        var matchedId = (bestMatch as JObject)?["id"]?.ToString();
                        if (!string.IsNullOrEmpty(matchedId))
                        {
                            var detailed = await LookupByImdbFromProvider(matchedId, provider);
                            if (detailed != null)
                            {
                                IMDBCache.CacheSearch(title, year, detailed.ImdbID);
                                searchResult.Movie = detailed;
                                searchResult.Source = provider.Name;
                                searchResult.Message = $"{provider.Name} match {detailed.Title} ({detailed.Year})";
                                return searchResult;
                            }

                            searchResult.ImdbApiFailed = true;
                            searchResult.AddMessage($"{provider.Name} details failed for {matchedId}");
                        }

                        // Only use a sparse search hit if it already contains a real
                        // IMDb rating. Otherwise continue to TMDb so we can at least
                        // resolve the correct movie/IMDb id without caching bad data.
                        var movie = ParseImdbApiResponse(bestMatch as JObject);

                        if (movie != null && movie.ImdbRating.HasValue)
                        {
                            IMDBCache.CacheMovie(movie);
                            IMDBCache.CacheSearch(title, year, movie.ImdbID);
                            movie.DataSource = $"{provider.Name} search";
                            searchResult.Movie = movie;
                            searchResult.Source = $"{provider.Name} search";
                            searchResult.Message = $"Sparse IMDb match {movie.Title} ({movie.Year}) via {provider.Name}";
                            return searchResult;
                        }

                        searchResult.AddMessage($"{provider.Name} search hit was incomplete; trying fallback");
                    }
                    else
                    {
                        searchResult.AddMessage($"{provider.Name} returned no titles for '{query}'");
                        LogManager.Debug($"{provider.Name} search returned no titles for '{query}'");
                    }
                }
                catch (Exception ex)
                {
                    var inner = ex.InnerException != null ? $" Inner: {ex.InnerException.Message}" : "";
                    searchResult.ImdbApiFailed = true;
                    searchResult.AddMessage($"{provider.Name} search '{query}' error: {ex.Message}{inner}");
                    LogManager.Error($"Error searching {provider.Name} for movie '{query}': {ex.Message}{inner}");
                }
            }

            var tmdbMovie = await SearchMovieViaTmdb(title, year, cacheDays, searchResult, true);
            if (tmdbMovie != null)
            {
                searchResult.Movie = tmdbMovie;
                if (string.IsNullOrWhiteSpace(searchResult.Source))
                    searchResult.Source = tmdbMovie.DataSource ?? "TMDb fallback";
                if (string.IsNullOrWhiteSpace(searchResult.Message))
                    searchResult.Message = $"TMDb fallback matched {tmdbMovie.Title} ({tmdbMovie.Year})";
                return searchResult;
            }

            IMDBCache.CacheNotFound(searchKey);
            if (string.IsNullOrWhiteSpace(searchResult.Message))
                searchResult.Message = $"No IMDb/TMDb data found for {title} ({year})";
            return searchResult;
        }

        private static IEnumerable<string> BuildSearchQueries(string title, int? year)
        {
            var queries = new List<string>();
            AddQuery(title);

            if (year.HasValue)
                AddQuery($"{title} {year.Value}");

            var compact = Regex.Replace(title, @"[^\w\s]", " ");
            compact = Regex.Replace(compact, @"\s+", " ").Trim();
            AddQuery(compact);

            return queries;

            void AddQuery(string query)
            {
                if (!string.IsNullOrWhiteSpace(query) &&
                    !queries.Contains(query, StringComparer.OrdinalIgnoreCase))
                {
                    queries.Add(query.Trim());
                }
            }
        }

        private static JArray ExtractSearchResults(JObject result)
        {
            if (result == null)
                return null;

            var direct = result["titles"] as JArray
                         ?? result["results"] as JArray
                         ?? result["items"] as JArray
                         ?? result["data"] as JArray
                         ?? result["data"]?["titles"] as JArray
                         ?? result["data"]?["results"] as JArray;

            if (direct != null)
                return direct;

            return FindTitleArray(result);
        }

        private static JArray FindTitleArray(JToken token)
        {
            if (token is JArray array &&
                array.OfType<JObject>().Any(IsTitleSearchObject))
            {
                return array;
            }

            foreach (var child in token.Children())
            {
                var found = FindTitleArray(child);
                if (found != null)
                    return found;
            }

            return null;
        }

        private static bool IsTitleSearchObject(JObject obj)
        {
            return obj != null &&
                   obj["id"] != null &&
                   (obj["primaryTitle"] != null ||
                    obj["originalTitle"] != null ||
                    obj["title"] != null ||
                    obj["name"] != null);
        }

        private static JToken PickBestMovieMatch(JArray results, string requestedTitle, int? requestedYear)
        {
            var objects = results.OfType<JObject>().ToList();
            if (!objects.Any())
                return results[0];

            var normalizedRequested = NormalizeTitle(requestedTitle);

            if (requestedYear.HasValue)
            {
                var exactYearMovie = objects.FirstOrDefault(r =>
                    IsMovieType(r) && GetStartYear(r) == requestedYear.Value);
                if (exactYearMovie != null)
                    return exactYearMovie;

                var exactYear = objects.FirstOrDefault(r => GetStartYear(r) == requestedYear.Value);
                if (exactYear != null)
                    return exactYear;
            }

            var exactTitleMovie = objects.FirstOrDefault(r =>
                IsMovieType(r) &&
                string.Equals(NormalizeTitle(GetTitle(r)), normalizedRequested, StringComparison.OrdinalIgnoreCase));
            if (exactTitleMovie != null)
                return exactTitleMovie;

            var firstMovie = objects.FirstOrDefault(IsMovieType);
            return firstMovie ?? objects[0];
        }

        private static bool IsMovieType(JObject item)
        {
            var type = item["type"]?.ToString()
                       ?? item["titleType"]?.ToString()
                       ?? item["kind"]?.ToString();

            return string.IsNullOrEmpty(type) ||
                   type.Equals("movie", StringComparison.OrdinalIgnoreCase) ||
                   type.Equals("feature", StringComparison.OrdinalIgnoreCase);
        }

        private static int? GetStartYear(JObject item)
        {
            return item["startYear"]?.ToObject<int?>()
                   ?? item["year"]?.ToObject<int?>()
                   ?? item["releaseYear"]?["year"]?.ToObject<int?>();
        }

        private static string GetTitle(JObject item)
        {
            return item["primaryTitle"]?.ToString()
                   ?? item["originalTitle"]?.ToString()
                   ?? item["title"]?.ToString()
                   ?? item["titleText"]?["text"]?.ToString()
                   ?? item["name"]?.ToString();
        }

        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return string.Empty;

            var normalized = Regex.Replace(title, @"[^\w]+", " ").Trim().ToLowerInvariant();
            return Regex.Replace(normalized, @"\s+", " ");
        }

        public static string NormalizeMovieProvider(string provider)
        {
            return string.Equals(provider, ProviderTmdb, StringComparison.OrdinalIgnoreCase)
                ? ProviderTmdb
                : ProviderTiffara;
        }

        public static string GetMovieProviderDisplayName(string provider)
        {
            return NormalizeMovieProvider(provider) == ProviderTmdb
                ? "TMDb"
                : "Tiffara";
        }

        // Cached provider: this was read + JSON-parsed from disk on every movie
        // lookup, which sits on the race path. Re-read only when settings.json
        // actually changes (checked via last-write time, no parse on the hot path).
        private static string _cachedMovieProvider;
        private static DateTime _cachedMovieProviderStamp;

        public static string GetSelectedMovieProvider()
        {
            try
            {
                if (!File.Exists(SETTINGS_FILE))
                    return ProviderTiffara;

                var stamp = File.GetLastWriteTimeUtc(SETTINGS_FILE);
                var cached = _cachedMovieProvider;
                if (cached != null && stamp == _cachedMovieProviderStamp)
                    return cached;

                var settings = JObject.Parse(File.ReadAllText(SETTINGS_FILE));
                var provider = settings["movie_api_provider"]?.ToString()
                               ?? settings["imdb_api_provider"]?.ToString();

                var normalized = NormalizeMovieProvider(provider);
                _cachedMovieProvider = normalized;
                _cachedMovieProviderStamp = stamp;
                return normalized;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error loading movie API provider: {ex.Message}");
                return ProviderTiffara;
            }
        }

        private static async Task<IMDBMovie> LookupByImdbViaTmdb(string imdbId)
        {
            var apiKey = GetTmdbApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
                return null;

            try
            {
                var findJson = await SendTmdbGetAsync(
                    $"/find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id",
                    apiKey);
                var movieResults = findJson?["movie_results"] as JArray;
                var tmdbMovieHit = movieResults?.OfType<JObject>().FirstOrDefault();
                var tmdbId = tmdbMovieHit?["id"]?.ToObject<int?>();
                if (!tmdbId.HasValue)
                    return null;

                var details = await SendTmdbGetAsync($"/movie/{tmdbId.Value}?language=en-US", apiKey);
                var movie = ParseTmdbMovie(details ?? tmdbMovieHit);
                if (movie != null && string.IsNullOrWhiteSpace(movie.ImdbID))
                    movie.ImdbID = imdbId;

                return movie;
            }
            catch (Exception ex)
            {
                LogManager.Error($"TMDb IMDb-id lookup error for '{imdbId}': {ex.Message}");
                return null;
            }
        }

        private static async Task<IMDBMovie> SearchMovieViaTmdb(
            string title,
            int? year,
            int cacheDays,
            MovieSearchResult searchResult,
            bool allowTiffaraDetails)
        {
            var apiKey = GetTmdbApiKey();
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                searchResult.AddMessage("TMDb skipped: no TMDb API key configured in Settings");
                return null;
            }

            try
            {
                var searchPath = $"/search/movie?query={Uri.EscapeDataString(title)}&include_adult=false&language=en-US&page=1";
                if (year.HasValue)
                    searchPath += $"&primary_release_year={year.Value}&year={year.Value}";

                var searchJson = await SendTmdbGetAsync(searchPath, apiKey);
                var tmdbResults = searchJson?["results"] as JArray;
                if (tmdbResults == null || !tmdbResults.Any())
                {
                    searchResult.TmdbFailed = true;
                    searchResult.AddMessage("TMDb fallback returned no movie results");
                    return null;
                }

                var bestTmdbMatch = PickBestTmdbMatch(tmdbResults, title, year) as JObject;
                var tmdbId = bestTmdbMatch?["id"]?.ToObject<int?>();
                if (!tmdbId.HasValue)
                {
                    searchResult.TmdbFailed = true;
                    searchResult.AddMessage("TMDb fallback result did not include a movie id");
                    return null;
                }

                var details = await SendTmdbGetAsync($"/movie/{tmdbId.Value}?language=en-US", apiKey);
                var tmdbMovie = ParseTmdbMovie(details ?? bestTmdbMatch);
                if (tmdbMovie == null)
                {
                    searchResult.TmdbFailed = true;
                    searchResult.AddMessage("TMDb fallback could not parse movie details");
                    return null;
                }

                searchResult.TmdbUsed = true;
                searchResult.Source = allowTiffaraDetails ? "TMDb fallback" : "TMDb";

                if (allowTiffaraDetails && !string.IsNullOrWhiteSpace(tmdbMovie.ImdbID))
                {
                    var imdbDetailed = await LookupByImdbFromProvider(tmdbMovie.ImdbID, TiffaraProvider);
                    if (imdbDetailed != null)
                    {
                        IMDBCache.CacheSearch(title, year, imdbDetailed.ImdbID);
                        imdbDetailed.DataSource = "TMDb fallback -> Tiffara";
                        searchResult.Source = imdbDetailed.DataSource;
                        searchResult.Message = $"TMDb found IMDb id {imdbDetailed.ImdbID}; Tiffara IMDb rating loaded";
                        return imdbDetailed;
                    }

                    searchResult.ImdbRatingUnavailable = true;
                    searchResult.AddMessage($"TMDb found IMDb id {tmdbMovie.ImdbID}, but Tiffara details are unavailable");
                }
                else
                {
                    searchResult.ImdbRatingUnavailable = true;
                    searchResult.AddMessage(
                        string.IsNullOrWhiteSpace(tmdbMovie.ImdbID)
                            ? "TMDb found the movie but did not provide an IMDb id"
                            : "TMDb selected: IMDb id found, but TMDb ratings are not used as IMDb ratings");
                }

                tmdbMovie.DataSource = allowTiffaraDetails ? "TMDb fallback metadata" : "TMDb metadata";
                return tmdbMovie;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException != null ? $" Inner: {ex.InnerException.Message}" : "";
                searchResult.TmdbFailed = true;
                searchResult.AddMessage($"TMDb fallback error: {ex.Message}{inner}");
                LogManager.Error($"TMDb fallback error for '{title}': {ex.Message}{inner}");
                return null;
            }
        }

        private static async Task<JObject> SendTmdbGetAsync(string pathAndQuery, string apiKey)
        {
            var useBearer = IsTmdbBearerToken(apiKey);
            var url = TMDB_API_BASE + pathAndQuery;

            if (!useBearer)
            {
                url += url.Contains("?") ? "&" : "?";
                url += $"api_key={Uri.EscapeDataString(apiKey)}";
            }

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                if (useBearer)
                {
                    var token = apiKey.Trim();
                    if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        token = token.Substring("Bearer ".Length).Trim();

                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }

                var response = await httpClient.SendAsync(request);
                var json = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"TMDb API error: {(int)response.StatusCode} {response.StatusCode} {TrimForLog(json)}");
                }

                return JObject.Parse(json);
            }
        }

        private static bool IsTmdbBearerToken(string apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                return false;

            var key = apiKey.Trim();
            return key.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
                   key.StartsWith("eyJ", StringComparison.Ordinal);
        }

        private static string TrimForLog(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            value = Regex.Replace(value, @"\s+", " ").Trim();
            return value.Length <= 180 ? value : value.Substring(0, 180) + "...";
        }

        private static JToken PickBestTmdbMatch(JArray results, string requestedTitle, int? requestedYear)
        {
            var objects = results.OfType<JObject>().ToList();
            if (!objects.Any())
                return results[0];

            var normalizedRequested = NormalizeTitle(requestedTitle);

            if (requestedYear.HasValue)
            {
                var yearMatch = objects.FirstOrDefault(r => GetTmdbYear(r) == requestedYear.Value);
                if (yearMatch != null)
                    return yearMatch;
            }

            var exactTitle = objects.FirstOrDefault(r =>
                string.Equals(NormalizeTitle(GetTmdbTitle(r)), normalizedRequested, StringComparison.OrdinalIgnoreCase));
            if (exactTitle != null)
                return exactTitle;

            return objects[0];
        }

        private static int? GetTmdbYear(JObject item)
        {
            var date = item["release_date"]?.ToString();
            if (!string.IsNullOrWhiteSpace(date) &&
                date.Length >= 4 &&
                int.TryParse(date.Substring(0, 4), out var year))
            {
                return year;
            }

            return null;
        }

        private static string GetTmdbTitle(JObject item)
        {
            return item["title"]?.ToString()
                   ?? item["original_title"]?.ToString()
                   ?? item["name"]?.ToString();
        }

        private static IMDBMovie ParseTmdbMovie(JObject json)
        {
            if (json == null)
                return null;

            var title = GetTmdbTitle(json);
            if (string.IsNullOrWhiteSpace(title))
                return null;

            var movie = new IMDBMovie
            {
                ImdbID = json["imdb_id"]?.ToString(),
                Title = title,
                Year = GetTmdbYear(json)?.ToString(),
                Plot = json["overview"]?.ToString(),
                Type = "movie",
                DataSource = "TMDb fallback metadata"
            };

            var runtime = json["runtime"]?.ToObject<int?>();
            if (runtime.HasValue && runtime.Value > 0)
                movie.Runtime = $"{runtime.Value} min";

            var genres = json["genres"] as JArray;
            if (genres != null && genres.Any())
            {
                movie.Genres = genres
                    .Select(g => g["name"]?.ToString())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
                movie.Genre = string.Join(", ", movie.Genres);
            }

            var spokenLanguages = json["spoken_languages"] as JArray;
            if (spokenLanguages != null && spokenLanguages.Any())
            {
                movie.Languages = spokenLanguages
                    .Select(l => l["english_name"]?.ToString() ?? l["name"]?.ToString())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
                movie.Language = string.Join(", ", movie.Languages);
            }

            var productionCountries = json["production_countries"] as JArray;
            if (productionCountries != null && productionCountries.Any())
            {
                movie.Countries = productionCountries
                    .Select(c => c["name"]?.ToString())
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .ToList();
                movie.Country = string.Join(", ", movie.Countries);
            }

            return movie;
        }

        public static string GetTmdbApiKey()
        {
            try
            {
                if (!File.Exists(SETTINGS_FILE))
                    return string.Empty;

                var settings = JObject.Parse(File.ReadAllText(SETTINGS_FILE));
                var stored = settings["tmdb_api_key"]?.ToString()
                             ?? settings["tmdb_key"]?.ToString()
                             ?? settings["tmdb_bearer_token"]?.ToString();

                return string.IsNullOrWhiteSpace(stored)
                    ? string.Empty
                    : RaceTrade.SecureConfig.Decrypt(stored).Trim();
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error loading TMDb API key: {ex.Message}");
                return string.Empty;
            }
        }

        /// <summary>
        /// Parse an IMDb-compatible API response into IMDBMovie object
        /// </summary>
        private static IMDBMovie ParseImdbApiResponse(JObject json)
        {
            if (json == null)
                return null;

            var movie = new IMDBMovie
            {
                ImdbID = json["id"]?.ToString(),
                Title = json["primaryTitle"]?.ToString()
                        ?? json["originalTitle"]?.ToString()
                        ?? json["title"]?.ToString()
                        ?? json["titleText"]?["text"]?.ToString()
                        ?? json["name"]?.ToString(),
                Year = json["startYear"]?.ToString()
                       ?? json["year"]?.ToString()
                       ?? json["releaseYear"]?["year"]?.ToString(),
                Runtime = ConvertRuntimeToString(json["runtimeSeconds"]?.ToObject<int?>()),
                Plot = json["plot"]?.ToString(),
                Poster = json["primaryImage"]?["url"]?.ToString() ?? json["primaryImage"]?.ToString(),
                Type = json["type"]?.ToString() ?? "movie",
                DataSource = "IMDb-compatible API"
            };

            // Parse rating
            // rounds to 1 decimal during parsing
            var ratingObj = json["rating"] as JObject;
            if (ratingObj != null)
            {
                var aggregateRating = ratingObj["aggregateRating"]?.ToObject<float?>();
                if (aggregateRating.HasValue)
                {
                    // Round to 1 decimal place to avoid float->double precision issues
                    movie.ImdbRating = Math.Round((double)aggregateRating.Value, 1);
                }

                var voteCount = ratingObj["voteCount"]?.ToObject<int?>();
                if (voteCount.HasValue)
                {
                    movie.ImdbVotes = voteCount.Value;
                }
            }

            // Parse genres array into comma-separated string
            var genresArray = json["genres"] as JArray;
            if (genresArray != null && genresArray.Any())
            {
                movie.Genres = genresArray.Select(g => g.ToString()).ToList();
                movie.Genre = string.Join(", ", movie.Genres);
            }

            // Parse directors
            var directorsArray = json["directors"] as JArray;
            if (directorsArray != null && directorsArray.Any())
            {
                var directorNames = directorsArray
                    .Select(d => d["displayName"]?.ToString())
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToList();

                movie.Director = string.Join(", ", directorNames);
            }

            // Parse stars/actors
            var starsArray = json["stars"] as JArray;
            if (starsArray != null && starsArray.Any())
            {
                var starNames = starsArray
                    .Select(s => s["displayName"]?.ToString())
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToList();

                movie.Actors = string.Join(", ", starNames);
            }

            // Parse origin countries
            var countriesArray = json["originCountries"] as JArray;
            if (countriesArray != null && countriesArray.Any())
            {
                movie.Countries = countriesArray
                    .Select(c => c["name"]?.ToString())
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToList();

                movie.Country = string.Join(", ", movie.Countries);
            }

            // Parse spoken languages
            var languagesArray = json["spokenLanguages"] as JArray;
            if (languagesArray != null && languagesArray.Any())
            {
                movie.Languages = languagesArray
                    .Select(l => l["name"]?.ToString())
                    .Where(name => !string.IsNullOrEmpty(name))
                    .ToList();

                movie.Language = string.Join(", ", movie.Languages);
            }

            return movie;
        }

        /// <summary>
        /// Convert runtime in seconds to human-readable string (e.g., "142 min")
        /// </summary>
        private static string ConvertRuntimeToString(int? runtimeSeconds)
        {
            if (!runtimeSeconds.HasValue || runtimeSeconds.Value == 0)
                return null;

            int minutes = runtimeSeconds.Value / 60;
            return $"{minutes} min";
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
            // (e.g. "Multitude") - same approach as IsInternal.
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
        /// Extract movie title from release name
        /// Extracts everything before the year
        /// </summary>
        public static string ExtractTitleFromRelease(string releaseName)
        {
            if (string.IsNullOrEmpty(releaseName))
                return null;

            var name = System.IO.Path.GetFileNameWithoutExtension(releaseName);

            // Extract everything before the year
            var match = Rx.TitleBeforeYear.Match(name);
            if (match.Success)
            {
                name = match.Groups[1].Value;
            }
            // If no year found, just use the whole name
            // The release name itself will be the title

            // Replace dots/underscores with spaces
            name = name.Replace('.', ' ').Replace('_', ' ');

            return name.Trim();
        }

        /// <summary>
        /// Extract year from release name
        /// </summary>
        public static int? ExtractYearFromRelease(string releaseName)
        {
            if (string.IsNullOrEmpty(releaseName))
                return null;

            // Look for 4-digit year (1900-2099)
            var match = Rx.Year.Match(releaseName);
            if (match.Success && int.TryParse(match.Groups[1].Value, out int year))
            {
                return year;
            }

            return null;
        }

        #endregion

        #region Release Validation

        /// <summary>
        /// Enriches release information with IMDB data
        /// </summary>
        public static async Task<MovieReleaseInfo> EnrichReleaseInfo(string releaseName)
        {
            var releaseInfo = new MovieReleaseInfo
            {
                ReleaseName = releaseName,
                IsValid = false
            };

            // Extract title and year, then search
            var title = ExtractTitleFromRelease(releaseName);
            var year = ExtractYearFromRelease(releaseName);

            if (!string.IsNullOrEmpty(title))
            {
                releaseInfo.Title = title;
                releaseInfo.Year = year;
                releaseInfo.Movie = await SearchMovie(title, year);
            }

            // Extract release metadata
            releaseInfo.Codec = ExtractCodec(releaseName);
            releaseInfo.Source = ExtractSource(releaseName);
            releaseInfo.Resolution = ExtractResolution(releaseName);
            releaseInfo.Range = ExtractRange(releaseName);
            releaseInfo.Group = ExtractGroup(releaseName);
            releaseInfo.RepeatTag = ExtractRepeatTag(releaseName);
            releaseInfo.IsInternal = IsInternal(releaseName);
            releaseInfo.IsMulti = IsMulti(releaseName);

            releaseInfo.IsValid = releaseInfo.Movie != null;

            return releaseInfo;
        }

        /// <summary>
        /// Check if release should be raced based on IMDB data and release metadata
        /// </summary>
        public static bool ShouldRace(MovieReleaseInfo releaseInfo, IMDBConfig config)
        {
            if (config == null)
                config = new IMDBConfig { FallbackOnError = true };

            var movie = releaseInfo?.Movie;

            // Check movie data
            if (movie != null)
            {
                // Check rating
                if (config.MinRating > 0 && movie.ImdbRating.HasValue)
                {
                    if (movie.ImdbRating.Value < config.MinRating)
                    {
                        LogManager.Warning($"  ❌ Rating {movie.ImdbRating.Value} below minimum {config.MinRating}");
                        return false;
                    }
                }
                else if (config.MinRating > 0)
                {
                    LogManager.Warning($"  ❌ No IMDb rating available while minimum {config.MinRating} is configured");
                    return false;
                }

                // Check votes
                if (config.MinVotes > 0 && movie.ImdbVotes.HasValue)
                {
                    if (movie.ImdbVotes.Value < config.MinVotes)
                    {
                        LogManager.Warning($"  ❌ Votes {movie.ImdbVotes.Value} below minimum {config.MinVotes}");
                        return false;
                    }
                }
                else if (config.MinVotes > 0)
                {
                    LogManager.Warning($"  ❌ No IMDb vote count available while minimum {config.MinVotes} is configured");
                    return false;
                }

                // Check genres - allowed
                if (config.AllowedGenres != null && config.AllowedGenres.Any())
                {
                    if (movie.Genres == null || !movie.Genres.Any())
                    {
                        LogManager.Warning("  ❌ No genre information available");
                        return config.FallbackOnError;
                    }

                    var hasAllowedGenre = movie.Genres.Any(g =>
                        config.AllowedGenres.Contains(g, StringComparer.OrdinalIgnoreCase));

                    if (!hasAllowedGenre)
                    {
                        LogManager.Warning($"  ❌ Genre not in allowed list: {string.Join(", ", movie.Genres)}");
                        return false;
                    }
                }

                // Check genres - blocked
                if (config.BlockedGenres != null && config.BlockedGenres.Any() && movie.Genres != null)
                {
                    var hasBlockedGenre = movie.Genres.Any(g =>
                        config.BlockedGenres.Contains(g, StringComparer.OrdinalIgnoreCase));

                    if (hasBlockedGenre)
                    {
                        LogManager.Warning($"  ❌ Genre is blocked: {string.Join(", ", movie.Genres)}");
                        return false;
                    }
                }

                if (config.NoDocumentary && HasGenre(movie, "Documentary"))
                {
                    LogManager.Warning("  ❌ Documentary genre blocked");
                    return false;
                }

                if (config.NoMusic && (HasGenre(movie, "Music") || HasGenre(movie, "Musical")))
                {
                    LogManager.Warning("  ❌ Music/Musical genre blocked");
                    return false;
                }

                if (config.NoComedy && HasGenre(movie, "Comedy"))
                {
                    LogManager.Warning("  ❌ Comedy genre blocked");
                    return false;
                }

                if (config.NoShow && !string.Equals(movie.Type, "movie", StringComparison.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"  ❌ IMDb title type '{movie.Type}' blocked by movies-only filter");
                    return false;
                }

                // Check language - only English
                if (config.OnlyEnglish && movie.Languages != null)
                {
                    if (!movie.Languages.Any(l => l.Equals("English", StringComparison.OrdinalIgnoreCase)))
                    {
                        LogManager.Warning($"  ❌ Not English language: {string.Join(", ", movie.Languages)}");
                        return false;
                    }
                }

                // Check country - only US
                if (config.OnlyUSCountry && movie.Countries != null)
                {
                    if (!movie.Countries.Any(c => c.Equals("United States", StringComparison.OrdinalIgnoreCase) ||
                                                  c.Equals("USA", StringComparison.OrdinalIgnoreCase)))
                    {
                        LogManager.Warning($"  ❌ Not US production: {string.Join(", ", movie.Countries)}");
                        return false;
                    }
                }
            }
            else if (!config.FallbackOnError)
            {
                LogManager.Warning("  ❌ No movie data found and FallbackOnError is false");
                return false;
            }

            // Check release metadata filters

            // Resolution
            if (config.AllowedResolutions != null && config.AllowedResolutions.Any())
            {
                if (string.IsNullOrEmpty(releaseInfo.Resolution) ||
                    !config.AllowedResolutions.Contains(releaseInfo.Resolution, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"  ❌ Resolution '{releaseInfo.Resolution}' not in allowed list");
                    return false;
                }
            }
            if (config.BlockedResolutions != null && config.BlockedResolutions.Any())
            {
                if (!string.IsNullOrEmpty(releaseInfo.Resolution) &&
                    config.BlockedResolutions.Contains(releaseInfo.Resolution, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"  ❌ Resolution '{releaseInfo.Resolution}' is blocked");
                    return false;
                }
            }

            // Source
            if (config.AllowedSources != null && config.AllowedSources.Any())
            {
                if (string.IsNullOrEmpty(releaseInfo.Source) ||
                    !config.AllowedSources.Contains(releaseInfo.Source, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"  ❌ Source '{releaseInfo.Source}' not in allowed list");
                    return false;
                }
            }
            if (config.BlockedSources != null && config.BlockedSources.Any())
            {
                if (!string.IsNullOrEmpty(releaseInfo.Source) &&
                    config.BlockedSources.Contains(releaseInfo.Source, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"  ❌ Source '{releaseInfo.Source}' is blocked");
                    return false;
                }
            }

            // Codec
            if (config.AllowedCodecs != null && config.AllowedCodecs.Any())
            {
                if (string.IsNullOrEmpty(releaseInfo.Codec) ||
                    !config.AllowedCodecs.Contains(releaseInfo.Codec, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"  ❌ Codec '{releaseInfo.Codec}' not in allowed list");
                    return false;
                }
            }
            if (config.BlockedCodecs != null && config.BlockedCodecs.Any())
            {
                if (!string.IsNullOrEmpty(releaseInfo.Codec) &&
                    config.BlockedCodecs.Contains(releaseInfo.Codec, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"  ❌ Codec '{releaseInfo.Codec}' is blocked");
                    return false;
                }
            }

            // Group
            if (config.AllowedGroups != null && config.AllowedGroups.Any())
            {
                if (string.IsNullOrEmpty(releaseInfo.Group) ||
                    !config.AllowedGroups.Contains(releaseInfo.Group, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"  ❌ Group '{releaseInfo.Group}' not in allowed list");
                    return false;
                }
            }
            if (config.BlockedGroups != null && config.BlockedGroups.Any())
            {
                if (!string.IsNullOrEmpty(releaseInfo.Group) &&
                    config.BlockedGroups.Contains(releaseInfo.Group, StringComparer.OrdinalIgnoreCase))
                {
                    LogManager.Warning($"  ❌ Group '{releaseInfo.Group}' is blocked");
                    return false;
                }
            }

            // Internal
            if (config.SkipInternals && releaseInfo.IsInternal)
            {
                LogManager.Warning("  ❌ Internal release (skipping internals)");
                return false;
            }
            if (config.OnlyInternals && !releaseInfo.IsInternal)
            {
                LogManager.Warning("  ❌ Not internal (only racing internals)");
                return false;
            }

            // HDR
            if (config.SkipHDR && !string.IsNullOrEmpty(releaseInfo.Range))
            {
                LogManager.Warning($"  ❌ HDR/DV release '{releaseInfo.Range}' (skipping HDR)");
                return false;
            }
            if (config.OnlyHDR && string.IsNullOrEmpty(releaseInfo.Range))
            {
                LogManager.Warning("  ❌ Not HDR (only racing HDR)");
                return false;
            }

            // Propers/Repacks
            if (config.SkipPropers && !string.IsNullOrEmpty(releaseInfo.RepeatTag))
            {
                LogManager.Warning($"  ❌ {releaseInfo.RepeatTag} release (skipping propers)");
                return false;
            }

            return true;
        }

        private static bool HasGenre(IMDBMovie movie, string genre)
        {
            return movie?.Genres?.Any(g => g.Equals(genre, StringComparison.OrdinalIgnoreCase)) == true;
        }

        #endregion
    }

    #region Data Models

    public class IMDBMovie
    {
        public string ImdbID { get; set; }
        public string Title { get; set; }
        public string Year { get; set; }
        public string Rated { get; set; }
        public string Released { get; set; }
        public string Runtime { get; set; }
        public string Genre { get; set; }
        public string Director { get; set; }
        public string Actors { get; set; }
        public string Plot { get; set; }
        public string Language { get; set; }
        public string Country { get; set; }
        public string Awards { get; set; }
        public string Poster { get; set; }
        public string Metascore { get; set; }
        public string Type { get; set; }
        public double? ImdbRating { get; set; }
        public int? ImdbVotes { get; set; }
        public string DataSource { get; set; }

        // Parsed lists
        public List<string> Genres { get; set; }
        public List<string> Languages { get; set; }
        public List<string> Countries { get; set; }
    }

    public class MovieSearchResult
    {
        private readonly List<string> messages = new List<string>();

        public string RequestedTitle { get; set; }
        public int? RequestedYear { get; set; }
        public IMDBMovie Movie { get; set; }
        public string Source { get; set; }
        public bool UsedCache { get; set; }
        public bool ImdbApiFailed { get; set; }
        public bool TmdbUsed { get; set; }
        public bool TmdbFailed { get; set; }
        public bool ImdbRatingUnavailable { get; set; }

        public string Message
        {
            get => messages.Count > 0 ? string.Join("; ", messages) : null;
            set
            {
                messages.Clear();
                if (!string.IsNullOrWhiteSpace(value))
                    messages.Add(value);
            }
        }

        public void AddMessage(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                messages.Add(message);
        }

        public static MovieSearchResult Failed(string title, int? year, string message)
        {
            var result = new MovieSearchResult
            {
                RequestedTitle = title,
                RequestedYear = year
            };
            result.AddMessage(message);
            return result;
        }
    }

    public class MovieReleaseInfo
    {
        public string ReleaseName { get; set; }
        public string ImdbId { get; set; }
        public string Title { get; set; }
        public int? Year { get; set; }
        public IMDBMovie Movie { get; set; }
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

    public class IMDBConfig
    {
        public bool Enabled { get; set; }
        public double MinRating { get; set; }
        public int MinVotes { get; set; }
        public List<string> AllowedGenres { get; set; }
        public List<string> BlockedGenres { get; set; }
        public bool OnlyEnglish { get; set; }
        public bool OnlyUSCountry { get; set; }
        public bool NoDocumentary { get; set; }
        public bool NoMusic { get; set; }
        public bool NoComedy { get; set; }
        public bool NoShow { get; set; }
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
