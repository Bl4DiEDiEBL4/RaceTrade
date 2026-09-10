using RaceTrade;
using RaceTrade.Engine.Logging;

namespace RaceTrade.Web.Services;

/// <summary>
/// Optional startup retry for failed FXP backend jobs from the persisted RaceTrade history.
/// Kept separate from the live racer so this only runs when explicitly enabled.
/// </summary>
public sealed class FxpBackendHardResetBacklogService : IHostedService, IDisposable
{
    private const int MaxStartupCandidates = 100;

    private readonly RaceHistoryStore _history;
    private readonly FxpBackendStore _fxpBackendStore;
    private CancellationTokenSource? _cts;
    private Task? _worker;

    public FxpBackendHardResetBacklogService(RaceHistoryStore history, FxpBackendStore fxpBackendStore)
    {
        _history = history;
        _fxpBackendStore = fxpBackendStore;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _worker = Task.Run(() => RunAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null || _worker is null) return;

        _cts.Cancel();

        try
        {
            await _worker.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Shutdown is allowed to cancel the startup scan.
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);

            if (!EngineSettings.AutoHardResetFxpBackendJobs)
                return;

            var lookbackHours = EngineSettings.FxpBackendHardResetLookbackHours;
            if (lookbackHours <= 0)
                return;

            var servers = _fxpBackendStore.Load().FxpBackends
                .Where(IsUsableServer)
                .ToList();

            if (servers.Count == 0)
            {
                LogManager.Warning("Startup FXP backend reset lookback enabled, but no usable FXP backends are configured.");
                return;
            }

            var cutoff = DateTimeOffset.Now.AddHours(-lookbackHours);
            var failed = _history.LatestReleases(500, status: "Failed")
                .Where(e => e.LastSeen >= cutoff)
                .Where(IsResetCandidate)
                .Where(e => !string.IsNullOrWhiteSpace(e.Release))
                .Take(MaxStartupCandidates)
                .ToList();

            if (failed.Count == 0)
            {
                LogManager.Info($"Startup FXP backend reset lookback ({lookbackHours}h): no failed FXP backend timeout jobs found.");
                return;
            }

            LogManager.Warning(
                $"Startup FXP backend reset lookback ({lookbackHours}h): checking {failed.Count} failed release(s). " +
                "Hard resets are rate-limited by the configured cooldown and max attempts.");

            foreach (var entry in failed)
            {
                cancellationToken.ThrowIfCancellationRequested();

                foreach (var server in servers)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!TryResolvePassword(server, out var password))
                        continue;

                    await FxpBackendRacer.TryHardResetSpreadJobAsync(
                        server.Host,
                        server.Port,
                        password,
                        DisplayName(server),
                        entry.Release,
                        $"startup lookback ({lookbackHours}h, failed at {entry.LastSeen:yyyy-MM-dd HH:mm:ss})",
                        cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            LogManager.Error($"Startup FXP backend reset lookback failed: {ex.Message}");
        }
    }

    private static bool IsResetCandidate(RaceHistoryStore.RaceReleaseSummary entry)
    {
        var text = $"{entry.Status} {entry.Reason} {entry.TargetSite}";

        return text.Contains("fxp_backend", StringComparison.OrdinalIgnoreCase)
               || text.Contains("timeout", StringComparison.OrdinalIgnoreCase)
               || text.Contains("timed out", StringComparison.OrdinalIgnoreCase)
               || text.Contains("transfer failed", StringComparison.OrdinalIgnoreCase)
               || text.Contains("aborted", StringComparison.OrdinalIgnoreCase)
               || text.Contains("not started", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsableServer(FxpBackend server) =>
        !server.Disabled
        && !string.IsNullOrWhiteSpace(server.Host)
        && !string.IsNullOrWhiteSpace(server.Port)
        && !string.IsNullOrWhiteSpace(server.Password);

    private static bool TryResolvePassword(FxpBackend server, out string password)
    {
        password = "";

        try
        {
            if (string.IsNullOrWhiteSpace(server.Password))
                return false;

            password = SecureConfig.IsEncrypted(server.Password) ||
                       server.Password.StartsWith("ENC:", StringComparison.OrdinalIgnoreCase)
                ? SecureConfig.Decrypt(server.Password)
                : server.Password;

            return !string.IsNullOrWhiteSpace(password);
        }
        catch (Exception ex)
        {
            LogManager.Error($"Startup FXP backend reset lookback skipped server '{DisplayName(server)}': {ex.Message}");
            return false;
        }
    }

    private static string DisplayName(FxpBackend server) =>
        string.IsNullOrWhiteSpace(server.Name)
            ? string.IsNullOrWhiteSpace(server.Id) ? server.Host : server.Id
            : server.Name;

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
