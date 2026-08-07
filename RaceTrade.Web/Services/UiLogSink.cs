using System.Collections.Concurrent;
using RaceTrade.Engine.Logging;

// Microsoft.Extensions.Logging.LogLevel is pulled in by ImplicitUsings in a web project,
// so the unqualified name is ambiguous. The engine's LogLevel is the only one this file
// deals with.
using LogLevel = RaceTrade.Engine.Logging.LogLevel;

namespace RaceTrade.Web.Services;

/// <summary>
/// The host's <see cref="ILogSink"/>: keeps a bounded in-memory buffer of recent engine
/// log events and notifies subscribed Blazor components when new ones arrive.
///
/// The ILogSink contract says Write must be non-blocking and thread-safe, because it is
/// called from the IRC receive path and the race path where added latency loses races.
/// So Write only does an enqueue and a counter bump; no locks are held, no UI work
/// happens here, and subscribers are notified through a throttled timer instead of
/// per-event (a busy race can produce hundreds of lines a second, and re-rendering a
/// Blazor component that often would flood the SignalR circuit).
/// </summary>
public sealed class UiLogSink : ILogSink, IDisposable
{
    private const int MaxEntries = 5000;

    private readonly RaceHistoryStore _history;
    private readonly ConcurrentQueue<LogEvent> _events = new();
    private readonly Timer _notifyTimer;
    private int _count;
    private long _pendingSinceLastNotify;

    /// <summary>Raised (on a timer thread) when new events have arrived.</summary>
    public event Action? Changed;

    public UiLogSink(RaceHistoryStore history)
    {
        _history = history;

        // Coalesce notifications to ~4/sec: enough to feel live, cheap enough that a
        // race storm cannot saturate the circuit.
        _notifyTimer = new Timer(_ =>
        {
            if (Interlocked.Exchange(ref _pendingSinceLastNotify, 0) == 0)
                return;

            try { Changed?.Invoke(); } catch { /* a broken subscriber must not kill logging */ }
        }, null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    public void Write(LogEvent entry)
    {
        if (entry is null) return;

        _history.Capture(entry);
        _events.Enqueue(entry);
        Interlocked.Increment(ref _pendingSinceLastNotify);

        // Trim without locking: the queue may briefly exceed MaxEntries, which is fine.
        if (Interlocked.Increment(ref _count) > MaxEntries)
        {
            if (_events.TryDequeue(out _))
                Interlocked.Decrement(ref _count);
        }
    }

    /// <summary>Most recent events, newest first, optionally filtered.</summary>
    public IReadOnlyList<LogEvent> Snapshot(int max = 300, LogChannel? channel = null, LogLevel? minLevel = null)
    {
        IEnumerable<LogEvent> q = _events.ToArray();

        if (channel.HasValue) q = q.Where(e => e.Channel == channel.Value);
        if (minLevel.HasValue) q = q.Where(e => e.Level >= minLevel.Value);

        return q.Reverse().Take(max).ToList();
    }

    public void Clear()
    {
        while (_events.TryDequeue(out _)) { }
        Interlocked.Exchange(ref _count, 0);
        Interlocked.Increment(ref _pendingSinceLastNotify);
    }

    public void Dispose() => _notifyTimer.Dispose();
}
