using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using RaceTrade;
using System.Linq;

public static class PreBotManager
{
    private static readonly HttpClient httpClient = new HttpClient();

    public static async Task<int> ImportFromPredbClubAsync(int count = 100)
    {
        try
        {
            LogManager.Info($"Importing {count} releases from predb.club...");

            string apiUrl = $"https://predb.club/api/v1/?count={count}";
            var response = await httpClient.GetStringAsync(apiUrl);
            var json = JObject.Parse(response);

            if (json["status"]?.ToString() != "success")
            {
                LogManager.Error("predb.club API returned non-success status");
                return 0;
            }

            var rows = json["data"]?["rows"] as JArray;
            if (rows == null || rows.Count == 0)
            {
                LogManager.Warning("No releases returned from predb.club");
                return 0;
            }

            // SORT BY preAt TIMESTAMP (oldest first, newest last)
            var sortedRows = rows
                .OrderBy(row => row["preAt"]?.ToObject<long>() ?? 0)
                .ToList();

            int imported = 0;
            int skipped = 0;

            // Import in chronological order
            foreach (var row in sortedRows)
            {
                try
                {
                    string releaseName = row["name"]?.ToString();
                    string category = row["cat"]?.ToString();
                    long preAtUnix = row["preAt"]?.ToObject<long>() ?? 0;

                    if (string.IsNullOrWhiteSpace(releaseName) || preAtUnix == 0)
                    {
                        skipped++;
                        continue;
                    }

                    // Convert Unix timestamp to DateTime
                    var preTime = DateTimeOffset.FromUnixTimeSeconds(preAtUnix).UtcDateTime;

                    // Store in database (oldest entries get lowest IDs)
                    await SQLiteHelper.StorePretimeAsync(releaseName, category, preTime);
                    imported++;

                    //  Log progress every 25 releases
                    if (imported % 25 == 0)
                    {
                        LogManager.Info($"Imported {imported}/{sortedRows.Count} releases...");
                    }
                }
                catch (Exception ex)
                {
                    LogManager.Error($"Error importing release: {ex.Message}");
                    skipped++;
                }
            }

            LogManager.Success($"Import complete: {imported} releases imported, {skipped} skipped");
            return imported;
        }
        catch (Exception ex)
        {
            LogManager.Error($"Error importing from predb.club: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Stores pretime when a PreBot announces a release
    /// </summary>
    public static async Task StorePretimeAsync(string releaseName, string section)
    {
        await SQLiteHelper.StorePretimeAsync(releaseName, section, DateTime.UtcNow);
    }

    /// <summary>
    /// Checks if release exceeds max pretime (returns true if OK to race)
    /// </summary>
    /// <summary>

    public static async Task<(bool allowed, int pretimeSeconds, string reason)> CheckMaxPretimeAsync(
        string releaseName,
        int? maxPretimeSeconds)
    {
        if (!maxPretimeSeconds.HasValue || maxPretimeSeconds.Value <= 0)
        {
            return (true, 0, "No max pretime configured");
        }

        var pretimeDiff = await SQLiteHelper.GetPretimeDifferenceSecondsAsync(releaseName);

        if (pretimeDiff == -1)
        {
            // No pretime found - allow
            return (true, -1, "No pretime found in database");
        }

        if (pretimeDiff > maxPretimeSeconds.Value)
        {
            // Too old
            return (false, pretimeDiff, $"Pretime {pretimeDiff}s exceeds max {maxPretimeSeconds}s");
        }

        // OK to race
        return (true, pretimeDiff, $"Pretime {pretimeDiff}s < {maxPretimeSeconds}s");
    }



}
