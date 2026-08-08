using System;
using System.Collections.Generic;

namespace RaceTrade
{
    /// <summary>
    /// Why a release was not raced. Kept coarse on purpose: this is the grouping you want
    /// to filter and count by. The precise trigger (which pattern, which rating) lives in
    /// <see cref="SkipRecord.Detail"/>.
    /// </summary>
    public enum SkipReason
    {
        Unknown = 0,

        // --- announce stage (whole release dropped before any site is considered) ---
        //
        // Deliberately NOT covered here: ignore-words and lines that match neither the
        // NEW nor PRE regex. Those fire for ordinary channel chatter, many times a
        // second, and would bury the actual decisions. They stay in the debug log.
        Duplicate,           // already processed, or already in flight
        NoSectionMapping,    // IRC section could not be mapped to a cbftp section

        // --- per-site stage ---
        SiteDisabled,
        SectionDisabled,     // section not in race_sections_enabled for that site
        NoCbftpMapping,      // sitebot mode: no cbftp -> IRC section mapping on that site
        Skiplist,            // section skiplist pattern matched
        Blacklist,           // per-site blacklist pattern matched
        GlobalBlacklist,
        Pretime,             // older than the allowed pretime
        Imdb,
        TvMaze,
        Rules,               // rule engine returned DROP

        // --- after the per-site loop ---
        NoSites,             // every site was filtered out
        InsufficientSites,   // fewer than the two sites a race needs

        Error
    }

    /// <summary>
    /// One rejection, with enough context to answer "why did it not race this?" without
    /// turning on debug logging and re-reading the whole log.
    ///
    /// Deliberately a plain immutable class rather than a record: the engine targets the
    /// same source as the old .NET Framework build.
    /// </summary>
    public sealed class SkipRecord
    {
        public DateTimeOffset At { get; } = DateTimeOffset.Now;

        /// <summary>Release that was rejected.</summary>
        public string Release { get; set; }

        /// <summary>Site the rejection applies to. Empty for announce-stage rejections.</summary>
        public string Site { get; set; }

        /// <summary>Site whose IRC announce triggered this, when known.</summary>
        public string SourceSite { get; set; }

        public string Section { get; set; }

        public SkipReason Reason { get; set; }

        /// <summary>
        /// The concrete trigger: the pattern that matched, "rating 4.2 &lt; 5.5", the rule
        /// that fired. This is the field that makes the feed worth reading.
        /// </summary>
        public string Detail { get; set; }

        public override string ToString() =>
            string.IsNullOrEmpty(Site)
                ? $"{Reason}: {Release} ({Detail})"
                : $"{Reason}: {Release} on {Site} ({Detail})";
    }

    /// <summary>
    /// Process-wide feed of skip decisions.
    ///
    /// A static event rather than an injected interface because the engine is ported
    /// .NET Framework code with static entry points everywhere (LogManager works the same
    /// way); threading a sink through every call site would be a much larger change for
    /// no benefit in a single-process app.
    ///
    /// Handlers are called straight from the IRC receive thread, so they MUST be
    /// non-blocking and thread-safe.
    /// </summary>
    public static class RaceDiagnostics
    {
        public static event Action<SkipRecord> Skipped;

        public static void Report(SkipRecord record)
        {
            if (record == null) return;

            try
            {
                Skipped?.Invoke(record);
            }
            catch
            {
                // A broken subscriber must never take down the race path.
            }
        }

        /// <summary>Convenience overload; also returns the record so callers can collect it.</summary>
        public static SkipRecord Report(
            string release,
            SkipReason reason,
            string detail,
            string site = null,
            string section = null,
            string sourceSite = null)
        {
            var record = new SkipRecord
            {
                Release = release,
                Reason = reason,
                Detail = detail,
                Site = site ?? "",
                Section = section ?? "",
                SourceSite = sourceSite ?? ""
            };

            Report(record);
            return record;
        }

        /// <summary>Reports every record in a batch (used when a FilterResult is finalised).</summary>
        public static void ReportAll(IEnumerable<SkipRecord> records)
        {
            if (records == null) return;

            foreach (var r in records)
                Report(r);
        }
    }
}
