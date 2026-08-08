using System.Collections.Concurrent;
using RaceTrade;

namespace RaceTrade.Web.Services;

/// <summary>
/// Ring buffer of the engine's skip decisions, feeding the Skips page.
///
/// Subscribes to the static <see cref="RaceDiagnostics.Skipped"/> event, which fires on
/// the IRC receive thread — so the write path is a lock-free enqueue and the UI is
/// notified on a timer rather than per record. A busy channel produces hundreds of skips
/// a minute; re-rendering per skip would peg a CPU core for no benefit.
/// </summary>
public sealed class SkipStore : IDisposable
{
    private const int MaxRecords = 2000;

    private readonly ConcurrentQueue<SkipRecord> _records = new();
    private int _notifyScheduled;
    private long _total;

    public SkipStore()
    {
        RaceDiagnostics.Skipped += OnSkipped;
    }

    public event Action? Changed;

    /// <summary>Everything skipped since startup, including what the buffer has dropped.</summary>
    public long Total => Interlocked.Read(ref _total);

    private void OnSkipped(SkipRecord record)
    {
        Interlocked.Increment(ref _total);
        _records.Enqueue(record);

        while (_records.Count > MaxRecords && _records.TryDequeue(out _)) { }

        ScheduleChanged();
    }

    private void ScheduleChanged()
    {
        // Coalesce: at most one UI notification per 250 ms no matter how many skips land.
        if (Interlocked.Exchange(ref _notifyScheduled, 1) == 1)
            return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            Interlocked.Exchange(ref _notifyScheduled, 0);

            try { Changed?.Invoke(); } catch { /* a dead circuit must not kill the feed */ }
        });
    }

    /// <summary>Newest first, optionally filtered. Returns a snapshot, safe to enumerate.</summary>
    public IReadOnlyList<SkipRecord> Recent(
        string? site = null,
        string? section = null,
        SkipReason? reason = null,
        string? search = null,
        int limit = 300)
    {
        var q = Filtered(site, section, search);

        if (reason is { } wanted)
            q = q.Where(r => r.Reason == wanted);

        return q.Take(limit).ToList();
    }

    /// <summary>
    /// Counts per reason, biggest first — the "what is eating my races" view.
    /// Takes the same site/section/search filters as <see cref="Recent"/> but no reason
    /// filter, so the chips stay meaningful while one of them is selected.
    /// </summary>
    public IReadOnlyList<(SkipReason Reason, int Count)> CountsByReason(
        string? site = null,
        string? section = null,
        string? search = null) =>
        Filtered(site, section, search)
            .GroupBy(r => r.Reason)
            .Select(g => (g.Key, g.Count()))
            .OrderByDescending(x => x.Item2)
            .ToList();

    private IEnumerable<SkipRecord> Filtered(string? site, string? section, string? search)
    {
        // ToArray() snapshots the queue; Reverse() puts newest first.
        IEnumerable<SkipRecord> q = _records.ToArray().Reverse();

        if (!string.IsNullOrWhiteSpace(site))
            q = q.Where(r => string.Equals(r.Site, site, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(section))
            q = q.Where(r => string.Equals(r.Section, section, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(search))
            q = q.Where(r =>
                (r.Release ?? "").Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (r.Detail ?? "").Contains(search, StringComparison.OrdinalIgnoreCase));

        return q;
    }

    public IReadOnlyList<string> Sites() =>
        _records.ToArray()
            .Select(r => r.Site)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<string> Sections() =>
        _records.ToArray()
            .Select(r => r.Section)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void Clear()
    {
        while (_records.TryDequeue(out _)) { }
        Changed?.Invoke();
    }

    public void Dispose() => RaceDiagnostics.Skipped -= OnSkipped;
}
