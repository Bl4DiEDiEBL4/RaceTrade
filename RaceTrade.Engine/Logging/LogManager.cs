using System;
using RaceTrade.Engine.Logging;

/// <summary>
/// Engine-side replacement for the WinForms LogManager.
///
/// The original static LogManager held references to the log Forms (ApplicationLog,
/// IrcLog, RaceLog, CBFTPIntegrationLog) and pushed entries straight into them, which
/// is what tied nearly every engine file to WinForms.
///
/// This keeps the exact same call surface — LogManager.Info/Error/LogCBFTP/... — so the
/// ~380 existing call sites compile unchanged, but routes everything to a pluggable
/// <see cref="ILogSink"/>. The host (Blazor, console, tests) installs the sink at
/// startup via <see cref="Configure"/>; until then output is discarded, so engine code
/// can never NRE on a missing logger.
///
/// Deliberately in the global namespace, matching the original, so no `using` changes
/// were needed across the ported files.
/// </summary>
public static class LogManager
{
    private static volatile ILogSink _sink = NullLogSink.Instance;

    /// <summary>Installs the sink that receives all engine log output.</summary>
    public static void Configure(ILogSink sink) => _sink = sink ?? NullLogSink.Instance;

    /// <summary>
    /// Forwards to <see cref="EngineSettings.DebugEnabled"/> on purpose. Engine code
    /// guards expensive debug work with `if (EngineSettings.DebugEnabled)` and then
    /// calls LogManager.Debug(...). If these were two separate flags, setting only one
    /// would make debug output vanish with no error.
    /// </summary>
    public static bool DebugEnabled
    {
        get => EngineSettings.DebugEnabled;
        set => EngineSettings.DebugEnabled = value;
    }

    public static bool DisableRaceLog { get; set; }
    public static bool DisableCbftpLog { get; set; }
    public static bool DisableApplicationLog { get; set; }

    private static void Emit(LogLevel level, LogChannel channel, string message,
        string site = null, string release = null)
    {
        // Never let a broken sink take down the race path.
        try
        {
            _sink.Write(new LogEvent
            {
                Level = level,
                Channel = channel,
                Message = message,
                Site = site,
                Release = release
            });
        }
        catch
        {
        }
    }

    // ---- Application-level convenience methods (same names as the WinForms build) ----

    public static void Info(string message)
    {
        if (DisableApplicationLog) return;
        Emit(LogLevel.Info, LogChannel.Application, message);
    }

    public static void Success(string message)
    {
        if (DisableApplicationLog) return;
        Emit(LogLevel.Success, LogChannel.Application, message);
    }

    public static void Warning(string message)
    {
        if (DisableApplicationLog) return;
        Emit(LogLevel.Warning, LogChannel.Application, message);
    }

    /// <summary>
    /// Errors are NOT gated by DisableApplicationLog: that switch is about quieting
    /// routine chatter, and silently swallowing errors would hide real failures.
    /// </summary>
    public static void Error(string message)
    {
        Emit(LogLevel.Error, LogChannel.Application, message);
    }

    /// <summary>Debug output is dropped entirely unless DebugEnabled is set.</summary>
    public static void Debug(string message)
    {
        if (!DebugEnabled) return;
        Emit(LogLevel.Debug, LogChannel.Application, message);
    }

    public static void Exception(Exception ex, string context = null)
    {
        var prefix = string.IsNullOrEmpty(context) ? "" : context + ": ";
        Emit(LogLevel.Error, LogChannel.Application, $"{prefix}{ex?.GetType().Name}: {ex?.Message}");
        if (DebugEnabled && ex?.StackTrace != null)
            Emit(LogLevel.Debug, LogChannel.Application, ex.StackTrace);
    }

    // ---- Structured channels ----

    public static void LogRace(RaceStatus status, string releaseName, string site,
        string targetSite = null, long size = 0, string quality = null,
        string filterReason = null, int? spreadJobId = null)
    {
        if (DisableRaceLog) return;

        var detail = status.ToString();
        if (!string.IsNullOrEmpty(quality)) detail += $" [{quality}]";
        if (!string.IsNullOrEmpty(targetSite)) detail += $" -> {targetSite}";
        if (!string.IsNullOrEmpty(filterReason)) detail += $" ({filterReason})";
        if (spreadJobId.HasValue) detail += $" job#{spreadJobId}";

        var level = status switch
        {
            RaceStatus.Failed => LogLevel.Error,
            RaceStatus.Filtered => LogLevel.Warning,
            RaceStatus.Completed => LogLevel.Success,
            _ => LogLevel.Info
        };

        Emit(level, LogChannel.Race, detail, site, releaseName);
    }

    public static void LogIRC(IRCEventType eventType, string message, string channel = null,
        string server = null, bool ruleMatched = false, string matchedRule = null)
    {
        var level = eventType == IRCEventType.Error ? LogLevel.Error : LogLevel.Info;
        var detail = message;
        if (!string.IsNullOrEmpty(channel)) detail = $"[{channel}] {detail}";
        if (ruleMatched && !string.IsNullOrEmpty(matchedRule)) detail += $" (rule: {matchedRule})";

        Emit(level, LogChannel.Irc, detail, server);
    }

    public static void LogCBFTP(CBFTPEventType eventType, string message, int? spreadJobId = null,
        string releaseName = null, string targetSite = null, int? progressPercent = null)
    {
        if (DisableCbftpLog) return;

        var level = eventType switch
        {
            CBFTPEventType.Error => LogLevel.Error,
            CBFTPEventType.SpreadJobFailed => LogLevel.Error,
            CBFTPEventType.SpreadJobCompleted => LogLevel.Success,
            _ => LogLevel.Info
        };

        var detail = message;
        if (spreadJobId.HasValue) detail += $" (job#{spreadJobId})";
        if (progressPercent.HasValue) detail += $" {progressPercent}%";

        Emit(level, LogChannel.Cbftp, detail, targetSite, releaseName);
    }
}

// ---- Enums carried over from the WinForms build (call sites depend on these names) ----

public enum RaceStatus
{
    Detected,
    Filtered,
    Racing,
    Completed,
    Failed
}

public enum IRCEventType
{
    Connection,
    Disconnection,
    Message,
    Announce,
    Error
}

public enum CBFTPEventType
{
    Info,
    Connected,
    Error,
    SpreadJobSent,
    SpreadJobStarted,
    SpreadJobCompleted,
    SpreadJobFailed
}
