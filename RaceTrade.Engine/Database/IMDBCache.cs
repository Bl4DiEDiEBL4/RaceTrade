using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using RaceTrade;

namespace RaceTrader
{
    /// <summary>
    /// Database cache for IMDB/OMDB API results
    /// Reduces API calls by caching movie information locally
    /// Database: db/imdb.db
    /// </summary>
    public static class IMDBCache
    {
        private static readonly string dbFolder = "db";
        private static readonly string dbFile = Path.Combine(dbFolder, "imdb.db");
        private static readonly string connectionString = $"Data Source={dbFile};";
        private static readonly object dbLock = new object();

        static IMDBCache()
        {
            InitializeDatabase();
        }
        public static void Initialize()
        {
            // Intentionally empty.
            // Calling this will trigger the static constructor once.
        }

        /// <summary>
        /// Initialize database and create tables if they don't exist
        /// </summary>
        private static void InitializeDatabase()
        {
            try
            {
                // Create db folder if it doesn't exist
                if (!Directory.Exists(dbFolder))
                {
                    Directory.CreateDirectory(dbFolder);
                    LogManager.Info($"Created database folder: {dbFolder}");
                }

                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        // Create movies table
                        var createMoviesTable = @"
                            CREATE TABLE IF NOT EXISTS imdb_movies (
                                imdb_id TEXT PRIMARY KEY,
                                title TEXT NOT NULL,
                                year TEXT,
                                rated TEXT,
                                released TEXT,
                                runtime TEXT,
                                genre TEXT,
                                director TEXT,
                                actors TEXT,
                                plot TEXT,
                                language TEXT,
                                country TEXT,
                                awards TEXT,
                                poster TEXT,
                                metascore TEXT,
                                imdb_rating REAL,
                                imdb_votes INTEGER,
                                type TEXT,
                                json_data TEXT,
                                last_updated TEXT NOT NULL,
                                created_at TEXT NOT NULL
                            )";

                        using (var cmd = new SqliteCommand(createMoviesTable, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // Create search cache table (for title searches)
                        var createSearchTable = @"
                            CREATE TABLE IF NOT EXISTS imdb_search_cache (
                                search_key TEXT PRIMARY KEY,
                                imdb_id TEXT,
                                title TEXT,
                                year TEXT,
                                last_updated TEXT NOT NULL
                            )";

                        using (var cmd = new SqliteCommand(createSearchTable, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // Create not found cache (to avoid repeated failed lookups)
                        var createNotFoundTable = @"
                            CREATE TABLE IF NOT EXISTS imdb_not_found (
                                search_key TEXT PRIMARY KEY,
                                last_checked TEXT NOT NULL
                            )";

                        using (var cmd = new SqliteCommand(createNotFoundTable, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // Create indexes
                        var createIndexes = @"
                            CREATE INDEX IF NOT EXISTS idx_title ON imdb_movies(title);
                            CREATE INDEX IF NOT EXISTS idx_year ON imdb_movies(year);
                            CREATE INDEX IF NOT EXISTS idx_rating ON imdb_movies(imdb_rating);
                            CREATE INDEX IF NOT EXISTS idx_votes ON imdb_movies(imdb_votes);
                            CREATE INDEX IF NOT EXISTS idx_updated ON imdb_movies(last_updated);
                        ";

                        using (var cmd = new SqliteCommand(createIndexes, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        LogManager.Info("IMDB database initialized successfully");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error initializing IMDB database: {ex.Message}");
            }
        }

        /// <summary>
        /// Get cached movie by IMDB ID
        /// </summary>
        public static IMDBMovie GetCachedMovieByImdb(string imdbId, int cacheDays = 30)
        {
            if (string.IsNullOrEmpty(imdbId))
                return null;

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var cutoffDate = DateTime.Now.AddDays(-cacheDays).ToString("yyyy-MM-dd HH:mm:ss");

                        var query = @"
                            SELECT * FROM imdb_movies 
                            WHERE imdb_id = @imdb_id 
                            AND last_updated > @cutoff
                            LIMIT 1";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@imdb_id", imdbId);
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    LogManager.Debug($"Cache HIT for IMDB: {imdbId}");
                                    return ParseMovieFromReader(reader);
                                }
                            }
                        }
                    }
                }

                LogManager.Debug($"Cache MISS for IMDB: {imdbId}");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error reading IMDB cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get cached movie by title and year
        /// </summary>
        public static IMDBMovie GetCachedMovieByTitle(string title, int? year = null, int cacheDays = 30)
        {
            if (string.IsNullOrEmpty(title))
                return null;

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var cutoffDate = DateTime.Now.AddDays(-cacheDays).ToString("yyyy-MM-dd HH:mm:ss");

                        // First check search cache
                        var searchKey = year.HasValue ? $"{title}|{year}" : title;
                        var searchQuery = @"
                            SELECT imdb_id FROM imdb_search_cache 
                            WHERE search_key = @search_key 
                            AND last_updated > @cutoff
                            LIMIT 1";

                        string cachedImdbId = null;
                        using (var cmd = new SqliteCommand(searchQuery, connection))
                        {
                            cmd.Parameters.AddWithValue("@search_key", searchKey);
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

                            var result = cmd.ExecuteScalar();
                            if (result != null)
                            {
                                cachedImdbId = result.ToString();
                            }
                        }

                        // If found in search cache, get the movie
                        if (!string.IsNullOrEmpty(cachedImdbId))
                        {
                            return GetCachedMovieByImdb(cachedImdbId, cacheDays);
                        }

                        // Try direct title match
                        string query;
                        if (year.HasValue)
                        {
                            query = @"
                                SELECT * FROM imdb_movies 
                                WHERE title = @title 
                                AND year = @year
                                AND last_updated > @cutoff
                                LIMIT 1";
                        }
                        else
                        {
                            query = @"
                                SELECT * FROM imdb_movies 
                                WHERE title = @title 
                                AND last_updated > @cutoff
                                ORDER BY imdb_rating DESC
                                LIMIT 1";
                        }

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@title", title);
                            if (year.HasValue)
                                cmd.Parameters.AddWithValue("@year", year.ToString());
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    LogManager.Debug($"Cache HIT for title: {title} ({year})");
                                    return ParseMovieFromReader(reader);
                                }
                            }
                        }
                    }
                }

                LogManager.Debug($"Cache MISS for title: {title} ({year})");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error reading IMDB cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Check if a search was recently not found (to avoid repeated API calls)
        /// </summary>
        public static bool IsRecentlyNotFound(string searchKey, int cacheDays = 7)
        {
            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var cutoffDate = DateTime.Now.AddDays(-cacheDays).ToString("yyyy-MM-dd HH:mm:ss");

                        var query = @"
                            SELECT COUNT(*) FROM imdb_not_found 
                            WHERE search_key = @search_key 
                            AND last_checked > @cutoff";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@search_key", searchKey);
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

                            int count = Convert.ToInt32(cmd.ExecuteScalar());
                            if (count > 0)
                            {
                                LogManager.Debug($"Recently not found (cached): {searchKey}");
                                return true;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error checking not found cache: {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Cache a movie in the database
        /// </summary>
        public static void CacheMovie(IMDBMovie movie)
        {
            if (movie == null || string.IsNullOrEmpty(movie.ImdbID))
                return;

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var query = @"
                            INSERT OR REPLACE INTO imdb_movies (
                                imdb_id, title, year, rated, released, runtime, genre, director,
                                actors, plot, language, country, awards, poster, metascore,
                                imdb_rating, imdb_votes, type, json_data, last_updated,
                                created_at
                            ) VALUES (
                                @imdb_id, @title, @year, @rated, @released, @runtime, @genre, @director,
                                @actors, @plot, @language, @country, @awards, @poster, @metascore,
                                @imdb_rating, @imdb_votes, @type, @json_data, @last_updated,
                                COALESCE((SELECT created_at FROM imdb_movies WHERE imdb_id = @imdb_id), @last_updated)
                            )";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@imdb_id", movie.ImdbID);
                            cmd.Parameters.AddWithValue("@title", movie.Title ?? "");
                            cmd.Parameters.AddWithValue("@year", movie.Year ?? "");
                            cmd.Parameters.AddWithValue("@rated", movie.Rated ?? "");
                            cmd.Parameters.AddWithValue("@released", movie.Released ?? "");
                            cmd.Parameters.AddWithValue("@runtime", movie.Runtime ?? "");
                            cmd.Parameters.AddWithValue("@genre", movie.Genre ?? "");
                            cmd.Parameters.AddWithValue("@director", movie.Director ?? "");
                            cmd.Parameters.AddWithValue("@actors", movie.Actors ?? "");
                            cmd.Parameters.AddWithValue("@plot", movie.Plot ?? "");
                            cmd.Parameters.AddWithValue("@language", movie.Language ?? "");
                            cmd.Parameters.AddWithValue("@country", movie.Country ?? "");
                            cmd.Parameters.AddWithValue("@awards", movie.Awards ?? "");
                            cmd.Parameters.AddWithValue("@poster", movie.Poster ?? "");
                            cmd.Parameters.AddWithValue("@metascore", movie.Metascore ?? "");
                            cmd.Parameters.AddWithValue("@imdb_rating", movie.ImdbRating ?? 0.0);
                            cmd.Parameters.AddWithValue("@imdb_votes", movie.ImdbVotes ?? 0);
                            cmd.Parameters.AddWithValue("@type", movie.Type ?? "");
                            cmd.Parameters.AddWithValue("@json_data", JsonConvert.SerializeObject(movie));
                            cmd.Parameters.AddWithValue("@last_updated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                            cmd.ExecuteNonQuery();
                        }

                        LogManager.Debug($"Cached movie: {movie.Title} ({movie.Year}) - {movie.ImdbID}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error caching IMDB movie: {ex.Message}");
            }
        }

        /// <summary>
        /// Cache a search result
        /// </summary>
        public static void CacheSearch(string title, int? year, string imdbId)
        {
            if (string.IsNullOrEmpty(title) || string.IsNullOrEmpty(imdbId))
                return;

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var searchKey = year.HasValue ? $"{title}|{year}" : title;

                        var query = @"
                            INSERT OR REPLACE INTO imdb_search_cache (
                                search_key, imdb_id, title, year, last_updated
                            ) VALUES (
                                @search_key, @imdb_id, @title, @year, @last_updated
                            )";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@search_key", searchKey);
                            cmd.Parameters.AddWithValue("@imdb_id", imdbId);
                            cmd.Parameters.AddWithValue("@title", title);
                            cmd.Parameters.AddWithValue("@year", year?.ToString() ?? "");
                            cmd.Parameters.AddWithValue("@last_updated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                            cmd.ExecuteNonQuery();
                        }

                        LogManager.Debug($"Cached search: {title} ({year}) → {imdbId}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error caching search: {ex.Message}");
            }
        }

        /// <summary>
        /// Cache a not found result
        /// </summary>
        public static void CacheNotFound(string searchKey)
        {
            if (string.IsNullOrEmpty(searchKey))
                return;

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var query = @"
                            INSERT OR REPLACE INTO imdb_not_found (
                                search_key, last_checked
                            ) VALUES (
                                @search_key, @last_checked
                            )";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@search_key", searchKey);
                            cmd.Parameters.AddWithValue("@last_checked", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                            cmd.ExecuteNonQuery();
                        }

                        LogManager.Debug($"Cached not found: {searchKey}");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error caching not found: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear old cache entries
        /// </summary>
        public static void ClearOldCache(int olderThanDays = 90)
        {
            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var cutoffDate = DateTime.Now.AddDays(-olderThanDays).ToString("yyyy-MM-dd HH:mm:ss");

                        var deleteMovies = "DELETE FROM imdb_movies WHERE last_updated < @cutoff";
                        using (var cmd = new SqliteCommand(deleteMovies, connection))
                        {
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);
                            int deleted = cmd.ExecuteNonQuery();
                            if (deleted > 0)
                                LogManager.Info($"Cleared {deleted} old IMDB movie entries");
                        }

                        var deleteSearches = "DELETE FROM imdb_search_cache WHERE last_updated < @cutoff";
                        using (var cmd = new SqliteCommand(deleteSearches, connection))
                        {
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);
                            int deleted = cmd.ExecuteNonQuery();
                            if (deleted > 0)
                                LogManager.Info($"Cleared {deleted} old IMDB search entries");
                        }

                        var deleteNotFound = "DELETE FROM imdb_not_found WHERE last_checked < @cutoff";
                        using (var cmd = new SqliteCommand(deleteNotFound, connection))
                        {
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);
                            int deleted = cmd.ExecuteNonQuery();
                            if (deleted > 0)
                                LogManager.Info($"Cleared {deleted} old not found entries");
                        }

                        // Vacuum to reclaim space
                        using (var cmd = new SqliteCommand("VACUUM", connection))
                        {
                            cmd.ExecuteNonQuery();
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error clearing old cache: {ex.Message}");
            }
        }

        /// <summary>
        /// Get cache statistics
        /// </summary>
        public static (int movies, int searches, int notFound, string dbSize) GetCacheStats()
        {
            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        int movies = 0;
                        using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM imdb_movies", connection))
                        {
                            movies = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        int searches = 0;
                        using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM imdb_search_cache", connection))
                        {
                            searches = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        int notFound = 0;
                        using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM imdb_not_found", connection))
                        {
                            notFound = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        var fileInfo = new FileInfo(dbFile);
                        var dbSize = fileInfo.Exists ? $"{fileInfo.Length / 1024.0:F2} KB" : "0 KB";

                        return (movies, searches, notFound, dbSize);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error getting cache stats: {ex.Message}");
                return (0, 0, 0, "Error");
            }
        }

        /// <summary>
        /// Get top rated cached movies
        /// </summary>
        public static List<IMDBMovie> GetTopRatedMovies(int limit = 10, double minRating = 7.0)
        {
            var movies = new List<IMDBMovie>();

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var query = @"
                            SELECT * FROM imdb_movies 
                            WHERE imdb_rating >= @min_rating 
                            ORDER BY imdb_rating DESC, imdb_votes DESC 
                            LIMIT @limit";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@min_rating", minRating);
                            cmd.Parameters.AddWithValue("@limit", limit);

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    movies.Add(ParseMovieFromReader(reader));
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error getting top rated movies: {ex.Message}");
            }

            return movies;
        }

        private static IMDBMovie ParseMovieFromReader(SqliteDataReader reader)
        {
            try
            {
                // Try to deserialize from JSON first (most complete data)
                var jsonData = reader["json_data"]?.ToString();
                if (!string.IsNullOrEmpty(jsonData))
                {
                    return JsonConvert.DeserializeObject<IMDBMovie>(jsonData);
                }

                // Fall back to manual parsing
                var movie = new IMDBMovie
                {
                    ImdbID = reader["imdb_id"]?.ToString(),
                    Title = reader["title"]?.ToString(),
                    Year = reader["year"]?.ToString(),
                    Rated = reader["rated"]?.ToString(),
                    Released = reader["released"]?.ToString(),
                    Runtime = reader["runtime"]?.ToString(),
                    Genre = reader["genre"]?.ToString(),
                    Director = reader["director"]?.ToString(),
                    Actors = reader["actors"]?.ToString(),
                    Plot = reader["plot"]?.ToString(),
                    Language = reader["language"]?.ToString(),
                    Country = reader["country"]?.ToString(),
                    Awards = reader["awards"]?.ToString(),
                    Poster = reader["poster"]?.ToString(),
                    Metascore = reader["metascore"]?.ToString(),
                    Type = reader["type"]?.ToString()
                };

                if (reader["imdb_rating"] != DBNull.Value)
                {
                    movie.ImdbRating = Convert.ToDouble(reader["imdb_rating"]);
                }

                if (reader["imdb_votes"] != DBNull.Value)
                {
                    movie.ImdbVotes = Convert.ToInt32(reader["imdb_votes"]);
                }

                // Parse genres
                if (!string.IsNullOrEmpty(movie.Genre))
                {
                    movie.Genres = movie.Genre.Split(',').Select(g => g.Trim()).ToList();
                }

                // Parse languages
                if (!string.IsNullOrEmpty(movie.Language))
                {
                    movie.Languages = movie.Language.Split(',').Select(l => l.Trim()).ToList();
                }

                // Parse countries
                if (!string.IsNullOrEmpty(movie.Country))
                {
                    movie.Countries = movie.Country.Split(',').Select(c => c.Trim()).ToList();
                }

                return movie;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error parsing movie from database: {ex.Message}");
                return null;
            }
        }
    }
}