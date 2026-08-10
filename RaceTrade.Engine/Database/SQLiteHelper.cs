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

    /// <summary>
    /// Initializes both SQLite databases and creates necessary tables.
    /// </summary>
    public static void InitializeDatabase()
    {
        try
        {
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
            Console.WriteLine($"Error initializing databases: {ex.Message}");
            Console.WriteLine($"Stack Trace: {ex.StackTrace}");
        }
    }

    private static void InitializeRacelogDatabase()
    {
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
            logEntries.Add($"Error loading logs from database: {ex.Message}");
        }

        return logEntries;
    }

    public static void LogProcessedRelease(string releaseName, string category, string siteName, long dateProcessed, long pretime)
    {
        try
        {
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
            Console.WriteLine($"Error logging processed release: {ex.Message}");
        }
    }

    public static async Task<bool> IsReleaseProcessedAsync(string releaseName)
    {
        try
        {
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
            Console.WriteLine($"Error checking processed release: {ex.Message}");
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
            LogManager.Error($"Error storing pretime for {releaseName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Gets pretime for a release (returns null if not found)
    /// </summary>
    public static async Task<DateTime?> GetPretimeAsync(string releaseName)
    {
        try
        {
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
            LogManager.Error($"Error getting pretime for {releaseName}: {ex.Message}");
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