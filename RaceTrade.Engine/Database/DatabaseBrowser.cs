using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RaceTrade;

public static class DatabaseBrowser
{
    private const int MaxLimit = 1000;

    public sealed record DatabaseSummary(
        int Pretimes,
        int ProcessedReleases,
        int ImdbMovies,
        int ImdbSearches,
        int ImdbNotFound,
        int TvMazeShows,
        int TvMazeEpisodes);

    public sealed record ImdbCacheEntry(
        string ImdbId,
        string Title,
        string Year,
        double? Rating,
        int? Votes,
        string Genre,
        string LastUpdated);

    public sealed record TvMazeCacheEntry(
        int TvMazeId,
        string Name,
        string Type,
        string Language,
        double? Rating,
        string Genres,
        string Network,
        string LastUpdated);

    public static DatabaseSummary GetSummary()
    {
        return new DatabaseSummary(
            SQLiteHelper.CountPretimes(),
            SQLiteHelper.CountProcessedReleases(),
            CountRows(Path.Combine("db", "imdb.db"), "imdb_movies"),
            CountRows(Path.Combine("db", "imdb.db"), "imdb_search_cache"),
            CountRows(Path.Combine("db", "imdb.db"), "imdb_not_found"),
            CountRows(Path.Combine("db", "tvmaze.db"), "tvmaze_shows"),
            CountRows(Path.Combine("db", "tvmaze.db"), "tvmaze_episodes"));
    }

    public static List<ImdbCacheEntry> SearchImdbMovies(string query, int limit)
    {
        var rows = new List<ImdbCacheEntry>();

        try
        {
            using var connection = Open(Path.Combine("db", "imdb.db"));
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT imdb_id, title, year, imdb_rating, imdb_votes, genre, last_updated
                FROM imdb_movies
                WHERE @query = ''
                   OR imdb_id LIKE @like
                   OR title LIKE @like
                   OR year LIKE @like
                   OR genre LIKE @like
                ORDER BY last_updated DESC
                LIMIT @limit;";
            AddSearchParameters(command, query, limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ImdbCacheEntry(
                    reader.IsDBNull(0) ? "" : reader.GetString(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3),
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6)));
            }
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error reading IMDB database browser rows: {SqliteRuntime.DescribeException(ex)}");
        }

        return rows;
    }

    public static List<TvMazeCacheEntry> SearchTvMazeShows(string query, int limit)
    {
        var rows = new List<TvMazeCacheEntry>();

        try
        {
            using var connection = Open(Path.Combine("db", "tvmaze.db"));
            using var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT tvmaze_id, name, type, language, rating, genres,
                       COALESCE(NULLIF(network, ''), web_channel, '') AS network_name,
                       last_updated
                FROM tvmaze_shows
                WHERE @query = ''
                   OR name LIKE @like
                   OR type LIKE @like
                   OR language LIKE @like
                   OR genres LIKE @like
                   OR network LIKE @like
                   OR web_channel LIKE @like
                   OR imdb_id LIKE @like
                ORDER BY last_updated DESC
                LIMIT @limit;";
            AddSearchParameters(command, query, limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new TvMazeCacheEntry(
                    reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetDouble(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5),
                    reader.IsDBNull(6) ? "" : reader.GetString(6),
                    reader.IsDBNull(7) ? "" : reader.GetString(7)));
            }
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error reading TVMaze database browser rows: {SqliteRuntime.DescribeException(ex)}");
        }

        return rows;
    }

    private static int CountRows(string dbFile, string table)
    {
        try
        {
            using var connection = Open(dbFile);
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(1) FROM {table}";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch
        {
            return 0;
        }
    }

    private static SqliteConnection Open(string dbFile)
    {
        SqliteRuntime.EnsureInitialized();
        Directory.CreateDirectory(Path.GetDirectoryName(dbFile) ?? ".");

        var connection = new SqliteConnection($"Data Source={dbFile};");
        connection.Open();
        return connection;
    }

    private static void AddSearchParameters(SqliteCommand command, string query, int limit)
    {
        var trimmed = query?.Trim() ?? "";
        command.Parameters.AddWithValue("@query", trimmed);
        command.Parameters.AddWithValue("@like", $"%{trimmed}%");
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, MaxLimit));
    }
}
