namespace RaceTrade.Web.Services;

/// <summary>
/// UI-facing wrapper around <see cref="EngineHost"/>: exposes running state and a
/// start/stop toggle, and raises <see cref="Changed"/> so open browser circuits
/// re-render. This is the web equivalent of the WinForms "TRADER STATUS" card.
///
/// Singleton: the racer is one process-wide thing, not per browser tab. Two people with
/// the UI open see the same state, and stopping it in one place stops it everywhere.
/// </summary>
public sealed class RacerState
{
    private readonly EngineHost _host;
    private int _busy; // 0 = idle, 1 = a start/stop is in flight

    public RacerState(EngineHost host) => _host = host;

    public event Action? Changed;

    public bool IsRunning => _host.IsRunning;

    /// <summary>True while a start/stop is in progress, so the UI can disable the button.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    public IReadOnlyCollection<string> ConnectedSites => _host.ConnectedSites;

    public async Task ToggleAsync()
    {
        // Guard against double-clicks and against two browsers toggling at once: without
        // this, a second Start could run while the first is still connecting sockets.
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
            return;

        Notify();

        try
        {
            if (_host.IsRunning)
                await _host.StopAsync();
            else
                await _host.StartAsync();
        }
        catch (Exception ex)
        {
            LogManager.Error($"Failed to {(_host.IsRunning ? "stop" : "start")} the racer: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
            Notify();
        }
    }

    private void Notify()
    {
        try { Changed?.Invoke(); } catch { /* a broken subscriber must not break the toggle */ }
    }
}
