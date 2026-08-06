using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Threading.Tasks;
using Newtonsoft.Json;
using RaceTrade;

namespace RaceTrader
{
    /// <summary>
    /// Database cache for TVMaze API results
    /// Reduces API calls by caching show information locally
    /// Database: db/tvmaze.db
    /// </summary>
    public static class TVMazeCache
    {
        private static readonly string dbFolder = "db";
        private static readonly string dbFile = Path.Combine(dbFolder, "tvmaze.db");
        private static readonly string connectionString = $"Data Source={dbFile};";
        private static readonly object dbLock = new object();

        static TVMazeCache()
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

                        // Create shows table
                        var createShowsTable = @"
                            CREATE TABLE IF NOT EXISTS tvmaze_shows (
                                tvmaze_id INTEGER PRIMARY KEY,
                                name TEXT NOT NULL,
                                type TEXT,
                                language TEXT,
                                status TEXT,
                                premiered TEXT,
                                runtime INTEGER,
                                rating REAL,
                                genres TEXT,
                                network TEXT,
                                web_channel TEXT,
                                imdb_id TEXT,
                                thetvdb_id INTEGER,
                                summary TEXT,
                                image_url TEXT,
                                json_data TEXT,
                                last_updated TEXT NOT NULL,
                                created_at TEXT NOT NULL
                            )";

                        using (var cmd = new SqliteCommand(createShowsTable, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // Create episodes table
                        var createEpisodesTable = @"
                            CREATE TABLE IF NOT EXISTS tvmaze_episodes (
                                episode_id INTEGER PRIMARY KEY,
                                show_id INTEGER NOT NULL,
                                name TEXT,
                                season INTEGER,
                                episode INTEGER,
                                airdate TEXT,
                                airtime TEXT,
                                airstamp TEXT,
                                runtime INTEGER,
                                summary TEXT,
                                json_data TEXT,
                                last_updated TEXT NOT NULL,
                                FOREIGN KEY (show_id) REFERENCES tvmaze_shows(tvmaze_id)
                            )";

                        using (var cmd = new SqliteCommand(createEpisodesTable, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        // Create indexes
                        var createIndexes = @"
                            CREATE INDEX IF NOT EXISTS idx_imdb ON tvmaze_shows(imdb_id);
                            CREATE INDEX IF NOT EXISTS idx_thetvdb ON tvmaze_shows(thetvdb_id);
                            CREATE INDEX IF NOT EXISTS idx_name ON tvmaze_shows(name);
                            CREATE INDEX IF NOT EXISTS idx_updated ON tvmaze_shows(last_updated);
                            CREATE INDEX IF NOT EXISTS idx_episodes_show ON tvmaze_episodes(show_id);
                        ";

                        using (var cmd = new SqliteCommand(createIndexes, connection))
                        {
                            cmd.ExecuteNonQuery();
                        }

                        LogManager.Info("TVMaze database initialized successfully");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error initializing TVMaze database: {ex.Message}");
            }
        }

        /// <summary>
        /// Get cached show by IMDB ID
        /// </summary>
        public static TVMazeShow GetCachedShowByImdb(string imdbId, int cacheDays = 7)
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
                            SELECT * FROM tvmaze_shows 
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
                                    return ParseShowFromReader(reader);
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
                LogManager.Error($"Error reading TVMaze cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get cached show by TVMaze ID
        /// </summary>
        public static TVMazeShow GetCachedShowById(int tvmazeId, int cacheDays = 7)
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
                            SELECT * FROM tvmaze_shows 
                            WHERE tvmaze_id = @id 
                            AND last_updated > @cutoff
                            LIMIT 1";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@id", tvmazeId);
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    LogManager.Debug($"Cache HIT for TVMaze ID: {tvmazeId}");
                                    return ParseShowFromReader(reader);
                                }
                            }
                        }
                    }
                }

                LogManager.Debug($"Cache MISS for TVMaze ID: {tvmazeId}");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error reading TVMaze cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Get cached show by name (fuzzy match)
        /// </summary>
        public static TVMazeShow GetCachedShowByName(string name, int cacheDays = 7)
        {
            if (string.IsNullOrEmpty(name))
                return null;

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var cutoffDate = DateTime.Now.AddDays(-cacheDays).ToString("yyyy-MM-dd HH:mm:ss");

                        // Try exact match first
                        var query = @"
                            SELECT * FROM tvmaze_shows 
                            WHERE name = @name 
                            AND last_updated > @cutoff
                            LIMIT 1";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@name", name);
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    LogManager.Debug($"Cache HIT for name: {name}");
                                    return ParseShowFromReader(reader);
                                }
                            }
                        }

                        // Try fuzzy match
                        var fuzzyQuery = @"
                            SELECT * FROM tvmaze_shows 
                            WHERE name LIKE @name 
                            AND last_updated > @cutoff
                            LIMIT 1";

                        using (var cmd = new SqliteCommand(fuzzyQuery, connection))
                        {
                            cmd.Parameters.AddWithValue("@name", $"%{name}%");
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

                            using (var reader = cmd.ExecuteReader())
                            {
                                if (reader.Read())
                                {
                                    LogManager.Debug($"Cache HIT (fuzzy) for name: {name}");
                                    return ParseShowFromReader(reader);
                                }
                            }
                        }
                    }
                }

                LogManager.Debug($"Cache MISS for name: {name}");
                return null;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error reading TVMaze cache: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Cache a show in the database
        /// </summary>
        public static void CacheShow(TVMazeShow show)
        {
            if (show == null)
                return;

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var query = @"
                            INSERT OR REPLACE INTO tvmaze_shows (
                                tvmaze_id, name, type, language, status, premiered, runtime,
                                rating, genres, network, web_channel, imdb_id, thetvdb_id,
                                summary, image_url, json_data, last_updated, created_at
                            ) VALUES (
                                @tvmaze_id, @name, @type, @language, @status, @premiered, @runtime,
                                @rating, @genres, @network, @web_channel, @imdb_id, @thetvdb_id,
                                @summary, @image_url, @json_data, @last_updated,
                                COALESCE((SELECT created_at FROM tvmaze_shows WHERE tvmaze_id = @tvmaze_id), @last_updated)
                            )";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@tvmaze_id", show.Id);
                            cmd.Parameters.AddWithValue("@name", show.Name ?? "");
                            cmd.Parameters.AddWithValue("@type", show.Type ?? "");
                            cmd.Parameters.AddWithValue("@language", show.Language ?? "");
                            cmd.Parameters.AddWithValue("@status", show.Status ?? "");
                            cmd.Parameters.AddWithValue("@premiered", show.Premiered ?? "");
                            cmd.Parameters.AddWithValue("@runtime", show.Runtime ?? 0);
                            cmd.Parameters.AddWithValue("@rating", show.Rating?.Average ?? 0.0);
                            cmd.Parameters.AddWithValue("@genres", string.Join(",", show.Genres ?? new List<string>()));
                            cmd.Parameters.AddWithValue("@network", show.Network?.Name ?? "");
                            cmd.Parameters.AddWithValue("@web_channel", show.WebChannel?.Name ?? "");
                            cmd.Parameters.AddWithValue("@imdb_id", show.Externals?.Imdb ?? "");
                            cmd.Parameters.AddWithValue("@thetvdb_id", show.Externals?.TheTvDb ?? 0);
                            cmd.Parameters.AddWithValue("@summary", show.Summary ?? "");
                            cmd.Parameters.AddWithValue("@image_url", show.Image?.Medium ?? "");
                            cmd.Parameters.AddWithValue("@json_data", JsonConvert.SerializeObject(show));
                            cmd.Parameters.AddWithValue("@last_updated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                            cmd.ExecuteNonQuery();
                        }

                        LogManager.Debug($"Cached show: {show.Name} (ID: {show.Id})");
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error caching TVMaze show: {ex.Message}");
            }
        }

        /// <summary>
        /// Get cached episodes for a show
        /// </summary>
        public static List<TVMazeEpisode> GetCachedEpisodes(int showId, int cacheDays = 7)
        {
            var episodes = new List<TVMazeEpisode>();

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var cutoffDate = DateTime.Now.AddDays(-cacheDays).ToString("yyyy-MM-dd HH:mm:ss");

                        var query = @"
                            SELECT * FROM tvmaze_episodes 
                            WHERE show_id = @show_id 
                            AND last_updated > @cutoff
                            ORDER BY season, episode";

                        using (var cmd = new SqliteCommand(query, connection))
                        {
                            cmd.Parameters.AddWithValue("@show_id", showId);
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);

                            using (var reader = cmd.ExecuteReader())
                            {
                                while (reader.Read())
                                {
                                    var episode = new TVMazeEpisode
                                    {
                                        Id = Convert.ToInt32(reader["episode_id"]),
                                        Name = reader["name"]?.ToString(),
                                        Season = reader["season"] != DBNull.Value ? Convert.ToInt32(reader["season"]) : 0,
                                        Number = reader["episode"] != DBNull.Value ? Convert.ToInt32(reader["episode"]) : (int?)null,
                                        AirDate = reader["airdate"]?.ToString(),
                                        AirTime = reader["airtime"]?.ToString(),
                                        Runtime = reader["runtime"] != DBNull.Value ? Convert.ToInt32(reader["runtime"]) : (int?)null,
                                        Summary = reader["summary"]?.ToString()
                                    };

                                    if (!string.IsNullOrEmpty(reader["airstamp"]?.ToString()))
                                    {
                                        episode.AirStamp = DateTime.Parse(reader["airstamp"].ToString());
                                    }

                                    episodes.Add(episode);
                                }
                            }
                        }

                        if (episodes.Count > 0)
                        {
                            LogManager.Debug($"Cache HIT for episodes: Show {showId} ({episodes.Count} episodes)");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error reading cached episodes: {ex.Message}");
            }

            return episodes;
        }

        /// <summary>
        /// Cache episodes for a show
        /// </summary>
        public static void CacheEpisodes(int showId, List<TVMazeEpisode> episodes)
        {
            if (episodes == null || episodes.Count == 0)
                return;

            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        using (var transaction = connection.BeginTransaction())
                        {
                            // Delete old episodes for this show
                            var deleteQuery = "DELETE FROM tvmaze_episodes WHERE show_id = @show_id";
                            using (var cmd = new SqliteCommand(deleteQuery, connection, transaction))
                            {
                                cmd.Parameters.AddWithValue("@show_id", showId);
                                cmd.ExecuteNonQuery();
                            }

                            // Insert new episodes
                            var insertQuery = @"
                                INSERT INTO tvmaze_episodes (
                                    episode_id, show_id, name, season, episode, airdate, airtime,
                                    airstamp, runtime, summary, json_data, last_updated
                                ) VALUES (
                                    @episode_id, @show_id, @name, @season, @episode, @airdate, @airtime,
                                    @airstamp, @runtime, @summary, @json_data, @last_updated
                                )";

                            foreach (var episode in episodes)
                            {
                                using (var cmd = new SqliteCommand(insertQuery, connection, transaction))
                                {
                                    cmd.Parameters.AddWithValue("@episode_id", episode.Id);
                                    cmd.Parameters.AddWithValue("@show_id", showId);
                                    cmd.Parameters.AddWithValue("@name", episode.Name ?? "");
                                    cmd.Parameters.AddWithValue("@season", episode.Season);
                                    cmd.Parameters.AddWithValue("@episode", episode.Number ?? 0);
                                    cmd.Parameters.AddWithValue("@airdate", episode.AirDate ?? "");
                                    cmd.Parameters.AddWithValue("@airtime", episode.AirTime ?? "");
                                    cmd.Parameters.AddWithValue("@airstamp", episode.AirStamp?.ToString("yyyy-MM-dd HH:mm:ss") ?? "");
                                    cmd.Parameters.AddWithValue("@runtime", episode.Runtime ?? 0);
                                    cmd.Parameters.AddWithValue("@summary", episode.Summary ?? "");
                                    cmd.Parameters.AddWithValue("@json_data", JsonConvert.SerializeObject(episode));
                                    cmd.Parameters.AddWithValue("@last_updated", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

                                    cmd.ExecuteNonQuery();
                                }
                            }

                            transaction.Commit();
                            LogManager.Debug($"Cached {episodes.Count} episodes for show {showId}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error caching episodes: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear old cache entries
        /// </summary>
        public static void ClearOldCache(int olderThanDays = 30)
        {
            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        var cutoffDate = DateTime.Now.AddDays(-olderThanDays).ToString("yyyy-MM-dd HH:mm:ss");

                        var deleteShows = "DELETE FROM tvmaze_shows WHERE last_updated < @cutoff";
                        using (var cmd = new SqliteCommand(deleteShows, connection))
                        {
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);
                            int deleted = cmd.ExecuteNonQuery();
                            if (deleted > 0)
                                LogManager.Info($"Cleared {deleted} old TVMaze show entries");
                        }

                        var deleteEpisodes = "DELETE FROM tvmaze_episodes WHERE last_updated < @cutoff";
                        using (var cmd = new SqliteCommand(deleteEpisodes, connection))
                        {
                            cmd.Parameters.AddWithValue("@cutoff", cutoffDate);
                            int deleted = cmd.ExecuteNonQuery();
                            if (deleted > 0)
                                LogManager.Info($"Cleared {deleted} old TVMaze episode entries");
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
        public static (int shows, int episodes, string dbSize) GetCacheStats()
        {
            try
            {
                lock (dbLock)
                {
                    using (var connection = new SqliteConnection(connectionString))
                    {
                        connection.Open();

                        int shows = 0;
                        using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM tvmaze_shows", connection))
                        {
                            shows = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        int episodes = 0;
                        using (var cmd = new SqliteCommand("SELECT COUNT(*) FROM tvmaze_episodes", connection))
                        {
                            episodes = Convert.ToInt32(cmd.ExecuteScalar());
                        }

                        var fileInfo = new FileInfo(dbFile);
                        var dbSize = fileInfo.Exists ? $"{fileInfo.Length / 1024.0:F2} KB" : "0 KB";

                        return (shows, episodes, dbSize);
                    }
                }
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error getting cache stats: {ex.Message}");
                return (0, 0, "Error");
            }
        }

        private static TVMazeShow ParseShowFromReader(SqliteDataReader reader)
        {
            try
            {
                // Try to deserialize from JSON first (most complete data)
                var jsonData = reader["json_data"]?.ToString();
                if (!string.IsNullOrEmpty(jsonData))
                {
                    return JsonConvert.DeserializeObject<TVMazeShow>(jsonData);
                }

                // Fall back to manual parsing
                var show = new TVMazeShow
                {
                    Id = Convert.ToInt32(reader["tvmaze_id"]),
                    Name = reader["name"]?.ToString(),
                    Type = reader["type"]?.ToString(),
                    Language = reader["language"]?.ToString(),
                    Status = reader["status"]?.ToString(),
                    Premiered = reader["premiered"]?.ToString(),
                    Runtime = reader["runtime"] != DBNull.Value ? Convert.ToInt32(reader["runtime"]) : (int?)null,
                    Summary = reader["summary"]?.ToString()
                };

                if (reader["rating"] != DBNull.Value)
                {
                    show.Rating = new TVMazeRating { Average = Convert.ToDouble(reader["rating"]) };
                }

                var genres = reader["genres"]?.ToString();
                if (!string.IsNullOrEmpty(genres))
                {
                    show.Genres = new List<string>(genres.Split(','));
                }

                var network = reader["network"]?.ToString();
                if (!string.IsNullOrEmpty(network))
                {
                    show.Network = new TVMazeNetwork { Name = network };
                }

                var webChannel = reader["web_channel"]?.ToString();
                if (!string.IsNullOrEmpty(webChannel))
                {
                    show.WebChannel = new TVMazeWebChannel { Name = webChannel };
                }

                var imdbId = reader["imdb_id"]?.ToString();
                var thetvdbId = reader["thetvdb_id"] != DBNull.Value ? Convert.ToInt32(reader["thetvdb_id"]) : (int?)null;
                if (!string.IsNullOrEmpty(imdbId) || thetvdbId.HasValue)
                {
                    show.Externals = new TVMazeExternals
                    {
                        Imdb = imdbId,
                        TheTvDb = thetvdbId
                    };
                }

                var imageUrl = reader["image_url"]?.ToString();
                if (!string.IsNullOrEmpty(imageUrl))
                {
                    show.Image = new TVMazeImage { Medium = imageUrl };
                }

                return show;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error parsing show from database: {ex.Message}");
                return null;
            }
        }
    }
}