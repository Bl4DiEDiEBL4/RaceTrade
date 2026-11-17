using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using RaceTrade;
using RaceTrader;

internal static class OptimizationRegressionTests
{
    internal static async Task Run()
    {
        RuleCachePreservesIsolationAndEdits();
        JobMonitorsAreScopedToBackendReleaseAndSection();
        await MetadataIsSharedWithoutSharingSiteDecisions();
        await MetadataPreservesFallbackAndCacheDurations();
        await LookupFallbackNotesMatchDecisions();
        await ExistingDatabaseGetsDuplicateIndex();
    }

    private static void JobMonitorsAreScopedToBackendReleaseAndSection()
    {
        var first = FxpBackendRacer.CreateJobMonitorKey("Release-GROUP", "TV", "backend-a.test", "59010");
        Check(first == FxpBackendRacer.CreateJobMonitorKey("Release-GROUP", "TV", "https://backend-a.test", "59010"),
            "Equivalent endpoint spellings must share one progress monitor.");
        Check(first != FxpBackendRacer.CreateJobMonitorKey("Release-GROUP", "TV", "backend-b.test", "59010"),
            "The same release and section on different backend hosts need independent monitors.");
        Check(first != FxpBackendRacer.CreateJobMonitorKey("Release-GROUP", "TV", "backend-a.test", "59011"),
            "Different backend ports need independent monitors.");
        Check(first != FxpBackendRacer.CreateJobMonitorKey("Release-GROUP", "MOVIES", "backend-a.test", "59010"),
            "Different sections need independent monitors.");
        Check(first != FxpBackendRacer.CreateJobMonitorKey("Other-GROUP", "TV", "backend-a.test", "59010"),
            "Different releases need independent monitors.");
    }

    private static void RuleCachePreservesIsolationAndEdits()
    {
        var config = JObject.Parse(@"{'sections':[{'irc_name':'TV','rules':[
            '[release] contains INTERNAL DROP', '[group] == TRUSTED EXCEPT'],
            'tags':[{'cbftp_section':'TV-HD','rules':['[release] contains 1080p ALLOW']}]}]}");
        var input = new Dictionary<string, string>
        {
            ["release"] = "Example.INTERNAL.1080p-TRUSTED", ["group"] = "TRUSTED"
        };
        Parallel.For(0, 38, _ =>
        {
            var engine = new RulesEngine();
            engine.LoadRulesForIrcSection(config, "TV", "TV-HD");
            Check(engine.Evaluate(input, "TV-HD") == "ALLOW", "EXCEPT must still override DROP.");
            engine._sectionRules[1].Value = "OTHER";
            Check(engine.Evaluate(input, "TV-HD") == "DROP", "Evaluator state must remain independent.");
        });

        var fresh = new RulesEngine();
        fresh.LoadRules(config, "TV-HD");
        Check(fresh.Evaluate(input, "TV-HD") == "ALLOW", "Mutable rules must not poison cached rules.");
        config["sections"][0]["rules"][1] = "[group] == OTHER EXCEPT";
        fresh.LoadRulesForIrcSection(config, "TV", "TV-HD");
        Check(fresh.Evaluate(input, "TV-HD") == "DROP", "Config edits must take effect immediately.");
    }

    private static async Task MetadataIsSharedWithoutSharingSiteDecisions()
    {
        var movieCalls = 0;
        var showCalls = 0;
        var lookup = new ReleaseMetadataLookup(
            () =>
            {
                Interlocked.Increment(ref movieCalls);
                return Task.FromResult(new MovieReleaseInfo
                {
                    Movie = new IMDBMovie { Title = "Example", ImdbRating = 7, Languages = new() { "English" } }
                });
            },
            _ =>
            {
                Interlocked.Increment(ref showCalls);
                return Task.FromResult(new TVShowReleaseInfo { Show = new TVMazeShow { Name = "Example", Language = "English" } });
            });
        await Task.WhenAll(Enumerable.Range(0, 38).Select(site => Task.Run(async () =>
        {
            var result = await RaceHelper.ValidateIMDB("Example.1080p-GROUP",
                new JObject { ["min_rating"] = site % 2 == 0 ? 6 : 8 }, "SITE" + site, lookup);
            Check((result == null) == (site % 2 == 0), "Site rating filters must remain independent.");
            Check(await RaceHelper.ValidateTVMaze("Example.1080p-GROUP", new JObject(), "SITE" + site, lookup) == null,
                "TVMaze data must remain usable by every site.");
        })));
        Check(movieCalls == 1 && showCalls == 1, "38 sites must share one lookup per metadata provider.");
    }

    private static async Task MetadataPreservesFallbackAndCacheDurations()
    {
        var showCalls = new List<int>();
        var movieCalls = 0;
        var lookup = new ReleaseMetadataLookup(() =>
        {
            movieCalls++;
            return Task.FromException<MovieReleaseInfo>(new InvalidOperationException("test lookup failure"));
        }, days =>
        {
            showCalls.Add(days);
            return Task.FromResult<TVShowReleaseInfo>(null);
        });
        Check(await RaceHelper.ValidateIMDB("Example-GROUP",
            new JObject { ["bypass_if_matches"] = new JArray("Example*") }, "BYPASS", lookup) == null,
            "Bypassed sites must not perform a lookup.");
        Check(movieCalls == 0, "Bypass must stay lazy.");
        Check(await RaceHelper.ValidateIMDB("Example-GROUP", new JObject(), "ALLOW", lookup) == null,
            "Fallback-on-error must still allow.");
        Check(await RaceHelper.ValidateIMDB("Example-GROUP",
            new JObject { ["fallback_on_error"] = false }, "BLOCK", lookup) != null,
            "Sites disabling fallback must still block the same lookup error.");
        Check(movieCalls == 1, "Lookup failures must not repeat for each site.");
        await lookup.GetShowAsync(7);
        await lookup.GetShowAsync(7);
        await lookup.GetShowAsync(1);
        Check(showCalls.SequenceEqual(new[] { 7, 1 }), "Different TVMaze cache durations must not share results.");
    }

    private static async Task LookupFallbackNotesMatchDecisions()
    {
        var missing = new ReleaseMetadataLookup(() => Task.FromResult<MovieReleaseInfo>(null),
            _ => Task.FromResult<TVShowReleaseInfo>(null));
        var imdbNotes = new List<string>();
        var tvNotes = new List<string>();
        Check(await RaceHelper.ValidateIMDB("Example-GROUP", new JObject(), "TEST", missing, imdbNotes) == null &&
            imdbNotes.Single().Contains("no data found"), "Default IMDb fallback must explicitly report missing data.");
        Check(await RaceHelper.ValidateTVMaze("Example-GROUP", new JObject(), "TEST", missing, tvNotes) == null &&
            tvNotes.Single().Contains("no data found"), "Default TVMaze fallback must explicitly report missing data.");

        var strict = new JObject { ["fallback_on_error"] = false };
        imdbNotes.Clear();
        tvNotes.Clear();
        Check(await RaceHelper.ValidateIMDB("Example-GROUP", strict, "TEST", missing, imdbNotes) != null && imdbNotes.Count == 0,
            "Strict IMDb filtering must block missing data without an allowed-fallback note.");
        Check(await RaceHelper.ValidateTVMaze("Example-GROUP", strict, "TEST", missing, tvNotes) != null && tvNotes.Count == 0,
            "Strict TVMaze filtering must block missing data without an allowed-fallback note.");

        var failed = new ReleaseMetadataLookup(() => Task.FromException<MovieReleaseInfo>(new InvalidOperationException("offline")),
            _ => Task.FromException<TVShowReleaseInfo>(new InvalidOperationException("offline")));
        Check(await RaceHelper.ValidateIMDB("Example-GROUP", new JObject(), "TEST", failed, imdbNotes) == null &&
            imdbNotes.Single().Contains("lookup failed"), "IMDb lookup failures allowed by fallback must be explained.");
        Check(await RaceHelper.ValidateTVMaze("Example-GROUP", new JObject(), "TEST", failed, tvNotes) == null &&
            tvNotes.Single().Contains("lookup failed"), "TVMaze lookup failures allowed by fallback must be explained.");
        Check(await RaceHelper.ValidateIMDB("Example-GROUP", strict, "TEST", failed) != null &&
            await RaceHelper.ValidateTVMaze("Example-GROUP", strict, "TEST", failed) != null,
            "Disabling fallback must still block provider exceptions.");

        imdbNotes.Clear();
        tvNotes.Clear();
        var valid = new ReleaseMetadataLookup(() => Task.FromResult(new MovieReleaseInfo { Movie = new IMDBMovie { Title = "Example" } }),
            _ => Task.FromResult(new TVShowReleaseInfo { Show = new TVMazeShow { Name = "Example" } }));
        Check(await RaceHelper.ValidateIMDB("Example-GROUP", strict, "TEST", valid, imdbNotes) == null && imdbNotes.Count == 0 &&
            await RaceHelper.ValidateTVMaze("Example-GROUP", strict, "TEST", valid, tvNotes) == null && tvNotes.Count == 0,
            "A genuine metadata pass must not be labelled as fallback.");
        var bypass = new JObject { ["fallback_on_error"] = false, ["bypass_if_matches"] = new JArray("Example*") };
        Check(await RaceHelper.ValidateIMDB("Example-GROUP", bypass, "TEST", failed, imdbNotes) == null &&
            imdbNotes.Single().Contains("bypass pattern"), "IMDb bypass must be reported as skipped, not as a metadata pass.");
        Check(await RaceHelper.ValidateTVMaze("Example-GROUP", bypass, "TEST", failed, tvNotes) == null &&
            tvNotes.Single().Contains("bypass pattern"), "TVMaze bypass must be reported as skipped, not as a metadata pass.");
    }

    private static async Task ExistingDatabaseGetsDuplicateIndex()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var testDirectory = Path.Combine(Path.GetTempPath(), "RaceTrade-index-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(testDirectory, "db"));
        try
        {
            Environment.CurrentDirectory = testDirectory;
            SqliteRuntime.EnsureInitialized();
            using (var connection = new SqliteConnection("Data Source=db/Racelog.db"))
            {
                connection.Open();
                using var seed = connection.CreateCommand();
                seed.CommandText = @"CREATE TABLE ProcessedReleases (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT, ReleaseName TEXT NOT NULL,
                    Category TEXT NOT NULL, SiteName TEXT NOT NULL, DateProcessed INTEGER NOT NULL, Pretime INTEGER);
                    INSERT INTO ProcessedReleases (ReleaseName,Category,SiteName,DateProcessed,Pretime)
                    VALUES ('Existing-GROUP','TV','SITEA',1,0),('Existing-GROUP','TV','SITEB',2,0);";
                seed.ExecuteNonQuery();
            }
            SQLiteHelper.InitializeDatabase();
            SQLiteHelper.InitializeDatabase();
            Check(SQLiteHelper.CountProcessedReleases() == 2, "Index migration must preserve old rows and duplicates.");
            Check(await SQLiteHelper.IsReleaseProcessedAsync("Existing-GROUP"), "Existing releases must still be detected.");
            Check(!await SQLiteHelper.IsReleaseProcessedAsync("Missing-GROUP"), "Unseen releases must remain new.");
            using var verify = new SqliteConnection("Data Source=db/Racelog.db;Pooling=False");
            verify.Open();
            using var explain = verify.CreateCommand();
            explain.CommandText = "EXPLAIN QUERY PLAN SELECT 1 FROM ProcessedReleases WHERE ReleaseName = 'Existing-GROUP' LIMIT 1";
            using var reader = explain.ExecuteReader();
            Check(reader.Read(), "Duplicate lookup must have a query plan.");
            var queryPlan = reader.GetString(3);
            Check(queryPlan.Contains("idx_processed_release_name", StringComparison.Ordinal),
                "Duplicate lookup must use the index rather than scan the table: " + queryPlan);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
