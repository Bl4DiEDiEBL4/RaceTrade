using System;

namespace RaceTrade.Engine.Logging
{
    /// <summary>
    /// Severity of an engine log message.
    /// The engine used to express this as a System.Drawing.Color chosen at each call
    /// site, which tied it to WinForms and made the meaning of a message a UI concern.
    /// The engine now states WHAT happened; the UI decides how to render it.
    /// </summary>
    public enum LogLevel
    {
        Debug,
        Info,
        Success,
        Warning,
        Error
    }

    /// <summary>
    /// Where an engine log message came from, so a UI can route messages to the
    /// right pane (IRC window, race log, cbftp log, ...) without parsing strings.
    /// </summary>
    public enum LogChannel
    {
        Application,
        Irc,
        Race,
        Cbftp,
        PreDb
    }

    /// <summary>
    /// A single log message emitted by the engine.
    /// </summary>
    public sealed class LogEvent
    {
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
        public LogLevel Level { get; set; }
        public LogChannel Channel { get; set; }

        /// <summary>Site the message relates to, when applicable.</summary>
        public string Site { get; set; }

        /// <summary>Release the message relates to, when applicable.</summary>
        public string Release { get; set; }

        public string Message { get; set; }

        public override string ToString() =>
            $"[{Timestamp:HH:mm:ss}] [{Level}] {(string.IsNullOrEmpty(Site) ? "" : "[" + Site + "] ")}{Message}";
    }

    /// <summary>
    /// The engine's only logging dependency. Implementations live in the host
    /// (Blazor UI, console, file, tests) — the engine never references a UI type.
    ///
    /// Implementations MUST be thread-safe and MUST NOT block: this is called from
    /// the IRC receive path and the race path, where added latency loses races.
    /// Do the formatting/dispatching work on your own thread.
    /// </summary>
    public interface ILogSink
    {
        void Write(LogEvent entry);
    }

    /// <summary>
    /// Convenience wrappers so engine code stays readable at the call site.
    /// </summary>
    public static class LogSinkExtensions
    {
        public static void Log(this ILogSink sink, LogLevel level, LogChannel channel, string message,
            string site = null, string release = null)
        {
            sink?.Write(new LogEvent
            {
                Level = level,
                Channel = channel,
                Message = message,
                Site = site,
                Release = release
            });
        }

        public static void Debug(this ILogSink sink, string message, LogChannel channel = LogChannel.Application, string site = null)
            => sink.Log(LogLevel.Debug, channel, message, site);

        public static void Info(this ILogSink sink, string message, LogChannel channel = LogChannel.Application, string site = null)
            => sink.Log(LogLevel.Info, channel, message, site);

        public static void Success(this ILogSink sink, string message, LogChannel channel = LogChannel.Application, string site = null)
            => sink.Log(LogLevel.Success, channel, message, site);

        public static void Warning(this ILogSink sink, string message, LogChannel channel = LogChannel.Application, string site = null)
            => sink.Log(LogLevel.Warning, channel, message, site);

        public static void Error(this ILogSink sink, string message, LogChannel channel = LogChannel.Application, string site = null)
            => sink.Log(LogLevel.Error, channel, message, site);
    }

    /// <summary>
    /// Discards everything. Useful in tests and as a safe default so engine code
    /// never has to null-check its sink.
    /// </summary>
    public sealed class NullLogSink : ILogSink
    {
        public static readonly NullLogSink Instance = new NullLogSink();
        public void Write(LogEvent entry) { }
    }
}
