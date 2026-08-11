using System.Text.RegularExpressions;
using Newtonsoft.Json;
using RaceTrade;
using RaceTrade.Engine.Logging;
using LogLevel = RaceTrade.Engine.Logging.LogLevel;

namespace RaceTrade.Web.Services;

/// <summary>
/// Persistent release/race history. The live log sink is intentionally short-lived;
/// this store keeps the useful race events across restarts in the configured data folder.
/// </summary>
public sealed class RaceHistoryStore : IDisposable
{
    private const string Dir = "history";
    private const string FilePath = "history/race_history.json";
    private const int MaxEntries = 10000;

    private static readonly string[] KnownStatuses = ["Detected", "Filtered", "Racing", "Completed", "Failed"];
    private static readonly Regex SectionRegex = new(@"\[(?<section>[^\]]+)\]", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Timer _flushTimer;
    private List<RaceHistoryEntry> _entries = [];
    private bool _dirty;

    public RaceHistoryStore()
    {
        Load();
        _flushTimer = new Timer(_ => FlushIfDirty(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public event Action? Changed;

    public void Capture(LogEvent entry)
    {
        if (entry.Channel != LogChannel.Race || string.IsNullOrWhiteSpace(entry.Release))
            return;

        var item = RaceHistoryEntry.FromLogEvent(entry);

        lock (_gate)
        {
            _entries.Insert(0, item);

            if (_entries.Count > MaxEntries)
                _entries.RemoveRange(MaxEntries, _entries.Count - MaxEntries);

            _dirty = true;
        }

        NotifyChanged();
    }

    public RaceHistoryStats GetStats()
    {
        lock (_gate)
        {
            var detected = _entries.Count(e => e.Status.Equals("Detected", StringComparison.OrdinalIgnoreCase));
            var unique = _entries
                .Where(e => !string.IsNullOrWhiteSpace(e.Release))
                .Select(e => e.Release)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();

            return new RaceHistoryStats(_entries.Count, detected, unique);
        }
    }

    public IReadOnlyList<RaceReleaseSummary> LatestReleases(int max = 100, string? query = null, string? status = null)
    {
        List<RaceHistoryEntry> snapshot;
        lock (_gate) snapshot = _entries.ToList();

        IEnumerable<RaceHistoryEntry> q = snapshot;

        if (!string.IsNullOrWhiteSpace(query))
        {
            var needle = query.Trim();
            q = q.Where(e =>
                e.Release.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                e.Site.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                e.Section.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                e.TargetSite.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                e.Reason.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(status))
            q = q.Where(e => e.Status.Equals(status, StringComparison.OrdinalIgnoreCase));

        return q.GroupBy(e => $"{e.Release}\0{e.Site}", StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var latest = g.OrderByDescending(e => e.Timestamp).First();
                return new RaceReleaseSummary(
                    latest.Timestamp,
                    latest.Release,
                    latest.Site,
                    latest.Status,
                    latest.Section,
                    latest.TargetSite,
                    ShouldShowReason(latest.Status) ? latest.Reason : "",
                    g.Count());
            })
            .OrderByDescending(e => e.LastSeen)
            .Take(max)
            .ToList();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _dirty = false;
        }

        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
            if (File.Exists(FilePath + ".bak")) File.Delete(FilePath + ".bak");
        }
        catch
        {
            // The next save will recreate the file if needed.
        }

        NotifyChanged();
    }

    public void Flush() => FlushIfDirty(force: true);

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;

            var loaded = JsonConvert.DeserializeObject<List<RaceHistoryEntry>>(File.ReadAllText(FilePath)) ?? [];
            _entries = loaded
                .Where(e => !string.IsNullOrWhiteSpace(e.Release))
                .OrderByDescending(e => e.Timestamp)
                .Take(MaxEntries)
                .ToList();
        }
        catch
        {
            _entries = [];
        }
    }

    private void FlushIfDirty(bool force = false)
    {
        List<RaceHistoryEntry> snapshot;

        lock (_gate)
        {
            if (!_dirty && !force) return;
            snapshot = _entries.ToList();
            _dirty = false;
        }

        try
        {
            Directory.CreateDirectory(Dir);
            AtomicFile.WriteAllText(FilePath, JsonConvert.SerializeObject(snapshot, Formatting.Indented));
        }
        catch
        {
            lock (_gate) _dirty = true;
        }
    }

    private void NotifyChanged()
    {
        try { Changed?.Invoke(); } catch { }
    }

    private static bool ShouldShowReason(string status) =>
        status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Filtered", StringComparison.OrdinalIgnoreCase);

    public void Dispose()
    {
        _flushTimer.Dispose();
        Flush();
    }

    public sealed record RaceHistoryEntry(
        DateTimeOffset Timestamp,
        string Release,
        string Site,
        string Status,
        string Section,
        string TargetSite,
        string Reason,
        string Message,
        LogLevel Level)
    {
        public static RaceHistoryEntry FromLogEvent(LogEvent entry)
        {
            var message = entry.Message ?? "";
            var status = !string.IsNullOrWhiteSpace(entry.Status)
                ? entry.Status
                : KnownStatuses.FirstOrDefault(s => message.StartsWith(s, StringComparison.OrdinalIgnoreCase)) ?? "Race";
            var section = !string.IsNullOrWhiteSpace(entry.Section)
                ? entry.Section
                : SectionRegex.Match(message).Groups["section"].Value;
            var reason = ShouldShowReason(status) ? entry.Reason ?? "" : "";
            var targetSite = entry.TargetSite ?? "";

            if (string.IsNullOrWhiteSpace(targetSite))
            {
                var arrow = message.IndexOf("->", StringComparison.Ordinal);
                if (arrow >= 0)
                {
                    targetSite = message[(arrow + 2)..].Trim();

                    var reasonStart = targetSite.IndexOf("(", StringComparison.Ordinal);
                    if (reasonStart >= 0) targetSite = targetSite[..reasonStart].Trim();

                    var jobStart = targetSite.IndexOf("job#", StringComparison.OrdinalIgnoreCase);
                    if (jobStart >= 0) targetSite = targetSite[..jobStart].Trim();
                }
            }

            return new RaceHistoryEntry(
                entry.Timestamp,
                entry.Release ?? "",
                entry.Site ?? "",
                status,
                section,
                targetSite,
                reason,
                message,
                entry.Level);
        }
    }

    public sealed record RaceReleaseSummary(
        DateTimeOffset LastSeen,
        string Release,
        string Site,
        string Status,
        string Section,
        string TargetSite,
        string Reason,
        int EventCount);

    public sealed record RaceHistoryStats(int Events, int Detected, int UniqueReleases);
}
