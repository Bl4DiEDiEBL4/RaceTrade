using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using RaceTrade;

public static class SQLiteHelper
{
    // SEPARATE DATABASE FILES
    private static readonly string RacelogDbFile = Path.Combine("db", "Racelog.db");
    private static readonly string PredbDbFile = Path.Combine("db", "Predb.db"); 

    private static readonly string RacelogConnectionString = $"Data Source={RacelogDbFile};";
    private static readonly string PredbConnectionString = $"Data Source={PredbDbFile};"; 

    public sealed record ProcessedReleaseEntry(
        int Id,
        string ReleaseName,
        string Category,
        string SiteName,
        long DateProcessed,
        long? Pretime);

    public sealed record PretimeEntry(
        int Id,
        string ReleaseName,
        string Section,
        long PreTimestamp,
        long CreatedAt);

    /// <summary>
    /// Initializes both SQLite databases and creates necessary tables.
    /// </summary>
    public static void InitializeDatabase()
    {
        try
        {
            SqliteRuntime.EnsureInitialized();

            // Ensure the directory exists
            var directory = Path.GetDirectoryName(RacelogDbFile);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Initialize Racelog database
            InitializeRacelogDatabase();

            // Initialize Predb database
            InitializePredbDatabase();

            Console.WriteLine("SQLite databases initialized successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error initializing databases: {SqliteRuntime.DescribeException(ex)}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
    }

    private static void InitializeRacelogDatabase()
    {
        SqliteRuntime.EnsureInitialized();
        using var connection = new SqliteConnection(RacelogConnectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS ProcessedReleases (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ReleaseName TEXT NOT NULL,
                Category TEXT NOT NULL,
                SiteName TEXT NOT NULL,
                DateProcessed INTEGER NOT NULL,
                Pretime INTEGER
            );
        ";
        command.ExecuteNonQuery();
    }

    private static void InitializePredbDatabase()
    {
        SqliteRuntime.EnsureInitialized();
        using var connection = new SqliteConnection(PredbConnectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = @"
            CREATE TABLE IF NOT EXISTS pretime (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                release_name TEXT NOT NULL UNIQUE,
                section TEXT NOT NULL,
                pre_timestamp INTEGER NOT NULL,
                created_at INTEGER NOT NULL
            );
            
            CREATE INDEX IF NOT EXISTS idx_release_name ON pretime(release_name);
            CREATE INDEX IF NOT EXISTS idx_section ON pretime(section);
        ";
        command.ExecuteNonQuery();
    }

    // ========================================
    // MAINTENANCE
    // ========================================

    /// <summary>Number of rows in the processed-releases (duplicate) table.</summary>
    public static int CountProcessedReleases() => CountRows(RacelogConnectionString, "ProcessedReleases");

    /// <summary>Number of stored pretimes.</summary>
    public static int CountPretimes() => CountRows(PredbConnectionString, "pretime");

    /// <summary>
    /// Empties the duplicate list. Everything announced afterwards is treated as new
    /// again, so a release already sitting on the target sites will be raced a second
    /// time and come back as a dupe there.
    /// </summary>
    public static int ClearProcessedReleases() => DeleteAll(RacelogConnectionString, "ProcessedReleases");

    /// <summary>Empties the pretime cache. It refills from the prebot announces.</summary>
    public static int ClearPretimes() => DeleteAll(PredbConnectionString, "pretime");

    private static int CountRows(string connectionString, string table)
    {
        try
        {
            SqliteRuntime.EnsureInitialized();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var command = connection.CreateCommand();
            // Table name is a compile-time constant from this file only, never user input.
            command.CommandText = $"SELECT COUNT(1) FROM {table}";
            return Convert.ToInt32(command.ExecuteScalar());
        }
        catch
        {
            // A missing database simply has nothing in it.
            return 0;
        }
    }

    private static int DeleteAll(string connectionString, string table)
    {
        SqliteRuntime.EnsureInitialized();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {table}";
        var removed = command.ExecuteNonQuery();

        // Reclaiming space is best-effort and must not be able to fail the operation.
        // The pretime table is written from the announce path, so a VACUUM can hit
        // SQLITE_BUSY - and the DELETE above has already committed by then, so throwing
        // would report a failure for something that actually worked.
        try
        {
            using var vacuum = connection.CreateCommand();
            vacuum.CommandText = "VACUUM";
            vacuum.ExecuteNonQuery();
        }
        catch
        {
        }

        return removed;
    }

    // ========================================
    // RACELOG DATABASE METHODS
    // ========================================

    public static List<string> GetAllLogEntries()
    {
        var logEntries = new List<string>();

        try
        {
            SqliteRuntime.EnsureInitialized();
            using var connection = new SqliteConnection(RacelogConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT ReleaseName
                FROM ProcessedReleases
                ORDER BY Id DESC
                LIMIT 100;
            ";

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                string release = reader.IsDBNull(0) ? "Unknown Release" : reader.GetString(0);
                logEntries.Add(release);
            }
        }
        catch (Exception ex)
        {
            logEntries.Add($"Error loading logs from database: {SqliteRuntime.DescribeException(ex)}");
        }

        return logEntries;
    }

    public static void LogProcessedRelease(string releaseName, string category, string siteName, long dateProcessed, long pretime)
    {
        try
        {
            SqliteRuntime.EnsureInitialized();
            using var connection = new SqliteConnection(RacelogConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO ProcessedReleases (ReleaseName, Category, SiteName, DateProcessed, Pretime)
                VALUES (@ReleaseName, @Category, @SiteName, @DateProcessed, @Pretime);
            ";
            command.Parameters.AddWithValue("@ReleaseName", releaseName);
            command.Parameters.AddWithValue("@Category", category);
            command.Parameters.AddWithValue("@SiteName", siteName);
            command.Parameters.AddWithValue("@DateProcessed", dateProcessed);
            command.Parameters.AddWithValue("@Pretime", pretime);

            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error logging processed release: {SqliteRuntime.DescribeException(ex)}");
        }
    }

    public static List<ProcessedReleaseEntry> SearchProcessedReleases(string query, int limit = 200)
    {
        var rows = new List<ProcessedReleaseEntry>();

        try
        {
            SqliteRuntime.EnsureInitialized();
            using var connection = new SqliteConnection(RacelogConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT Id, ReleaseName, Category, SiteName, DateProcessed, Pretime
                FROM ProcessedReleases
                WHERE @query = ''
                   OR ReleaseName LIKE @like
                   OR Category LIKE @like
                   OR SiteName LIKE @like
                ORDER BY Id DESC
                LIMIT @limit;
            ";
            command.Parameters.AddWithValue("@query", query?.Trim() ?? "");
            command.Parameters.AddWithValue("@like", $"%{query?.Trim() ?? ""}%");
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ProcessedReleaseEntry(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetString(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4),
                    reader.IsDBNull(5) ? null : reader.GetInt64(5)));
            }
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error reading processed releases: {SqliteRuntime.DescribeException(ex)}");
        }

        return rows;
    }

    public static async Task<bool> IsReleaseProcessedAsync(string releaseName)
    {
        try
        {
            SqliteRuntime.EnsureInitialized();
            using var connection = new SqliteConnection(RacelogConnectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(1) FROM ProcessedReleases WHERE ReleaseName = @ReleaseName";
            command.Parameters.AddWithValue("@ReleaseName", releaseName);

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result) > 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error checking processed release: {SqliteRuntime.DescribeException(ex)}");
            return false;
        }
    }

    // ========================================
    // PREDB DATABASE METHODS
    // ========================================

    /// <summary>
    /// Stores pretime for a release (FIRST-WINS - ignores duplicates)
    /// Uses MILLISECOND precision for accurate pretime tracking
    /// </summary>
    public static async Task StorePretimeAsync(string releaseName, string section, DateTime preTimestamp)
    {
        try
        {
            SqliteRuntime.EnsureInitialized();
            using var connection = new SqliteConnection(PredbConnectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT OR IGNORE INTO pretime (release_name, section, pre_timestamp, created_at)
                VALUES (@releaseName, @section, @preTimestamp, @createdAt)
            ";

            command.Parameters.AddWithValue("@releaseName", releaseName);
            command.Parameters.AddWithValue("@section", section);
            command.Parameters.AddWithValue("@preTimestamp", ((DateTimeOffset)preTimestamp).ToUnixTimeMilliseconds());
            command.Parameters.AddWithValue("@createdAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            int rowsAffected = await command.ExecuteNonQueryAsync();

            if (rowsAffected > 0)
            {
                LogManager.Debug($"Stored pretime for [{releaseName}] in section [{section}]");
            }
            else
            {
                LogManager.Debug($"Pretime already exists for [{releaseName}], keeping first timestamp");
            }
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error storing pretime for {releaseName}: {SqliteRuntime.DescribeException(ex)}");
        }
    }

    public static List<PretimeEntry> SearchPretimes(string query, int limit = 200)
    {
        var rows = new List<PretimeEntry>();

        try
        {
            SqliteRuntime.EnsureInitialized();
            using var connection = new SqliteConnection(PredbConnectionString);
            connection.Open();

            var command = connection.CreateCommand();
            command.CommandText = @"
                SELECT id, release_name, section, pre_timestamp, created_at
                FROM pretime
                WHERE @query = ''
                   OR release_name LIKE @like
                   OR section LIKE @like
                ORDER BY pre_timestamp DESC
                LIMIT @limit;
            ";
            command.Parameters.AddWithValue("@query", query?.Trim() ?? "");
            command.Parameters.AddWithValue("@like", $"%{query?.Trim() ?? ""}%");
            command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 1000));

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new PretimeEntry(
                    reader.GetInt32(0),
                    reader.IsDBNull(1) ? "" : reader.GetString(1),
                    reader.IsDBNull(2) ? "" : reader.GetString(2),
                    reader.IsDBNull(3) ? 0 : reader.GetInt64(3),
                    reader.IsDBNull(4) ? 0 : reader.GetInt64(4)));
            }
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error reading pretimes: {SqliteRuntime.DescribeException(ex)}");
        }

        return rows;
    }

    /// <summary>
    /// Gets pretime for a release (returns null if not found)
    /// </summary>
    public static async Task<DateTime?> GetPretimeAsync(string releaseName)
    {
        try
        {
            SqliteRuntime.EnsureInitialized();
            using var connection = new SqliteConnection(PredbConnectionString);
            await connection.OpenAsync();

            var command = connection.CreateCommand();
            command.CommandText = "SELECT pre_timestamp FROM pretime WHERE release_name = @releaseName LIMIT 1";
            command.Parameters.AddWithValue("@releaseName", releaseName);

            var result = await command.ExecuteScalarAsync();

            if (result != null && result != DBNull.Value)
            {
                long unixTimestampMs = Convert.ToInt64(result);
                return DateTimeOffset.FromUnixTimeMilliseconds(unixTimestampMs).UtcDateTime;
            }
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error getting pretime for {releaseName}: {SqliteRuntime.DescribeException(ex)}");
        }

        return null;
    }

    /// <summary>
    /// Calculates pretime difference in seconds (returns -1 if not found)
    /// </summary>
    public static async Task<int> GetPretimeDifferenceSecondsAsync(string releaseName)
    {
        var preTime = await GetPretimeAsync(releaseName);

        if (preTime == null)
            return -1;

        var difference = DateTime.UtcNow - preTime.Value;
        return (int)difference.TotalSeconds;
    }
}
