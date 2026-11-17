using Microsoft.Data.Sqlite;

internal static class ReleaseSendRegressionTests
{
    internal static async Task Run()
    {
        var originalDirectory = Environment.CurrentDirectory;
        var directory = Path.Combine(Path.GetTempPath(), "RaceTrade-send-test-" + Guid.NewGuid().ToString("N"));
        const string release = "Example.Release.1080p-GROUP";
        Directory.CreateDirectory(directory);
        try
        {
            Environment.CurrentDirectory = directory;
            SQLiteHelper.InitializeDatabase();
            var claims = await Task.WhenAll(Enumerable.Range(0, 32)
                .Select(_ => Task.Run(() => FxpBackendRacer.TryBeginSendAsync(release))));
            Check(claims.Count(c => c) == 1, "Only one simultaneous announce may send a release.");
            Check(!await FxpBackendRacer.TryBeginSendAsync(release.ToUpperInvariant()),
                "An in-flight release must also reject differently cased repeats.");

            FxpBackendRacer.EndSend(release);
            Check(await FxpBackendRacer.TryBeginSendAsync(release), "Failed sends must remain retryable.");
            SQLiteHelper.LogProcessedRelease(release, "TV", "SITE-A,SITE-B", 1, 0, "BOT-A");
            FxpBackendRacer.EndSend(release);
            Check(!await FxpBackendRacer.TryBeginSendAsync(release),
                "An announce delayed until the first send finishes must not resend a processed release.");
            SQLiteHelper.ClearProcessedReleases();
            Check(await FxpBackendRacer.TryBeginSendAsync(release),
                "Rejecting a processed release must release the claim for future retries after a reset.");
            Check(!await FxpBackendRacer.TryBeginSendAsync(" "), "Blank releases must not be claimed.");
        }
        finally
        {
            FxpBackendRacer.EndSend(release);
            SqliteConnection.ClearAllPools();
            Environment.CurrentDirectory = originalDirectory;
            Directory.Delete(directory, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
