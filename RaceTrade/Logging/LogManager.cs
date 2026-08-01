using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;

namespace RaceTrade
{
    #region Enums

    public enum LogLevel
    {
        Debug,
        Info,
        Success,
        Warning,
        Error,
        Critical
    }

    public enum LogCategory
    {
        Race,
        IRC,
        CBFTP,
        Application
    }

    public enum RaceStatus
    {
        Detected,
        Filtered,
        Queued,
        Racing,
        Completed,
        Failed,
        Cancelled
    }

    public enum IRCEventType
    {
        Connection,
        Disconnection,
        ChannelJoin,
        ChannelPart,
        Announce,
        EncryptionStatus,
        Error,
        Message
    }

    public enum CBFTPEventType
    {
        Info,
        Connected,
        Disconnected,
        SpreadJobSent,
        SpreadJobStarted,
        SpreadJobProgress,
        SpreadJobCompleted,
        SpreadJobFailed,
        Error
    }

    #endregion

    #region Log Entries

    public class LogEntry
    {
        public DateTime Timestamp { get; set; }
        public LogLevel Level { get; set; }
        public LogCategory Category { get; set; }
        public string Message { get; set; }
        public Color DisplayColor { get; set; }
        public Dictionary<string, object> Metadata { get; set; }

        public LogEntry()
        {
            Timestamp = DateTime.Now;
            Metadata = new Dictionary<string, object>();
        }

        public virtual string GetFormattedMessage()
        {
            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] {Message}";
        }
    }

    public class RaceLogEntry : LogEntry
    {
        public string ReleaseName { get; set; }
        public string Site { get; set; }
        public RaceStatus Status { get; set; }
        public string TargetSite { get; set; }
        public long Size { get; set; }
        public string Quality { get; set; }
        public string FilterReason { get; set; }
        public int? SpreadJobId { get; set; }

        public RaceLogEntry()
        {
            Category = LogCategory.Race;
            DisplayColor = Color.White;
        }

        public override string GetFormattedMessage()
        {
            var parts = new List<string>();

            // [2025-11-30 22:07:52]
            parts.Add($"[{Timestamp:yyyy-MM-dd HH:mm:ss}]");

            // Only show origin site for DETECTED
            if (Status == RaceStatus.Detected && !string.IsNullOrEmpty(Site))
            {
                parts.Add($"[{Site}]");
            }

            // Section: [MP3], [TV-1080P], [XXX-0DAY], etc.
            if (!string.IsNullOrEmpty(Quality))
            {
                parts.Add($"[{Quality}]");
            }

            // For non-Detected: show race sites list instead ([SITEA,SITEB,SITEC])
            if (Status != RaceStatus.Detected &&
                !string.IsNullOrEmpty(TargetSite) &&
                TargetSite != "CBFTP")
            {
                parts.Add($"[{TargetSite}]");
            }

            // Status: [DETECTED], [RACING], [COMPLETED], [FAILED], ...
            parts.Add($"[{Status.ToString().ToUpper()}]");

            // Release name
            if (!string.IsNullOrEmpty(ReleaseName))
            {
                parts.Add(ReleaseName);
            }

            // Optional filter reason
            if (!string.IsNullOrEmpty(FilterReason))
            {
                parts.Add($"({FilterReason})");
            }

            return string.Join(" :: ", parts);
        }


        // ⭐ Get color based on status and section
        // ⭐ Get color based on status and section
        public Color GetLogColor()
        {
            switch (Status)
            {
                case RaceStatus.Detected:
                    return Color.Cyan;

                case RaceStatus.Racing:
                    return Color.FromArgb(255, 140, 0); // Dark Orange instead of Yellow

                case RaceStatus.Completed:
                    return GetSectionColor(Quality);

                case RaceStatus.Filtered:
                    return Color.Orange;

                case RaceStatus.Failed:
                    return Color.Red;

                default:
                    return Color.White;
            }
        }

        // ⭐ Color coding by section type
        private Color GetSectionColor(string section)
        {
            if (string.IsNullOrEmpty(section))
                return Color.LightGreen;

            string upperSection = section.ToUpper();

            // Movies - X264/X265/BLURAY (Cornflower Blue)
            if (upperSection.Contains("X264") || upperSection.Contains("X265") ||
                upperSection.Contains("BLURAY") || upperSection.Contains("DVDR") ||
                upperSection.Contains("MBLURAY") || upperSection.Contains("MDVDR"))
            {
                return Color.FromArgb(80, 180, 255); // Cornflower Blue
            }

            // TV Shows (Light Green)
            if (upperSection.StartsWith("TV-"))
            {
                return Color.FromArgb(144, 238, 144); // Light Green
            }

            // Music - MP3/FLAC (Violet)
            if (upperSection.Contains("MP3") || upperSection.Contains("FLAC") ||
                upperSection.Contains("MVID"))
            {
                return Color.FromArgb(238, 130, 238); // Violet
            }

            // XXX (Hot Pink)
            if (upperSection.StartsWith("XXX-"))
            {
                return Color.FromArgb(255, 105, 180); // Hot Pink
            }

            // eBooks (Wheat)
            if (upperSection.Contains("EBOOK"))
            {
                return Color.FromArgb(245, 222, 179); // Wheat
            }

            // Games (Gold)
            if (upperSection.Contains("GAMES") || upperSection.Contains("NSW") ||
                upperSection.Contains("PS5"))
            {
                return Color.FromArgb(255, 215, 0); // Gold
            }

            // Apps/0DAY (Light Coral)
            if (upperSection.Contains("0DAY") || upperSection.Contains("APPS"))
            {
                return Color.FromArgb(240, 128, 128); // Light Coral
            }

            // Default for unknown sections
            return Color.FromArgb(211, 211, 211); // Light Gray
        }

        private string GetStatusString()
        {
            return Status switch
            {
                RaceStatus.Detected => "[DETECTED]",
                RaceStatus.Filtered => "[FILTERED]",
                RaceStatus.Queued => "[QUEUED]",
                RaceStatus.Racing => "[RACING]",
                RaceStatus.Completed => "[COMPLETE]",
                RaceStatus.Failed => "[FAILED]",
                RaceStatus.Cancelled => "[CANCELLED]",
                _ => "[UNKNOWN]"
            };
        }

        private string GetDetailsString()
        {
            var parts = new List<string>();

            if (!string.IsNullOrEmpty(Quality))
                parts.Add(Quality);

            if (Size > 0)
                parts.Add(FormatSize(Size));

            if (!string.IsNullOrEmpty(TargetSite))
                parts.Add($"→ {TargetSite}");

            if (!string.IsNullOrEmpty(FilterReason))
                parts.Add($"({FilterReason})");

            if (SpreadJobId.HasValue)
                parts.Add($"Job#{SpreadJobId}");

            return parts.Count > 0 ? " - " + string.Join(", ", parts) : "";
        }

        private string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##}{sizes[order]}";
        }

        public Color GetStatusColor()
        {
            return Status switch
            {
                RaceStatus.Detected => Color.Magenta,
                RaceStatus.Filtered => Color.Gray,
                RaceStatus.Queued => Color.Orange,
                RaceStatus.Racing => Color.Yellow,
                RaceStatus.Completed => Color.LightGreen,
                RaceStatus.Failed => Color.LightCoral,
                RaceStatus.Cancelled => Color.DarkGray,
                _ => Color.White
            };
        }
    }

    public class IRCLogEntry : LogEntry
    {
        public IRCEventType EventType { get; set; }
        public string Channel { get; set; }
        public string Server { get; set; }
        public bool RuleMatched { get; set; }
        public string MatchedRule { get; set; }

        public IRCLogEntry()
        {
            Category = LogCategory.IRC;
            DisplayColor = Color.White;
        }

        public override string GetFormattedMessage()
        {
            var prefix = GetEventPrefix();
            var channelStr = !string.IsNullOrEmpty(Channel) ? $" [{Channel}]" : "";
            var ruleStr = RuleMatched && !string.IsNullOrEmpty(MatchedRule)
                ? $" (matched: {MatchedRule})"
                : "";

            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}]{channelStr} {prefix} {Message}{ruleStr}";
        }

        private string GetEventPrefix()
        {
            return EventType switch
            {

                IRCEventType.Connection => "[CONNECT]",
                IRCEventType.Disconnection => "[DISCONNECT]",
                IRCEventType.ChannelJoin => "[JOIN]",
                IRCEventType.ChannelPart => "[PART]",
                IRCEventType.Announce => "[ANNOUNCE]",
                IRCEventType.EncryptionStatus => "[ENCRYPT]",
                IRCEventType.Error => "[ERROR]",
                IRCEventType.Message => "[MSG]",
                _ => "[INFO]"
            };
        }

        public Color GetEventColor()
        {
            return EventType switch
            {
                IRCEventType.Connection => Color.LightGreen,
                IRCEventType.Disconnection => Color.Orange,
                IRCEventType.ChannelJoin => Color.LightBlue,
                IRCEventType.Announce => RuleMatched ? Color.Yellow : Color.White,
                IRCEventType.EncryptionStatus => Color.Cyan,
                IRCEventType.Error => Color.LightCoral,
                _ => Color.White
            };
        }
    }

    public class CBFTPLogEntry : LogEntry
    {
        public CBFTPEventType EventType { get; set; }
        public int? SpreadJobId { get; set; }
        public string ReleaseName { get; set; }
        public string TargetSite { get; set; }
        public int? ProgressPercent { get; set; }
        public string ErrorMessage { get; set; }

        public CBFTPLogEntry()
        {
            Category = LogCategory.CBFTP;
            DisplayColor = Color.White;
        }

        public override string GetFormattedMessage()
        {
            var prefix = GetEventPrefix();
            var jobStr = SpreadJobId.HasValue ? $" Job#{SpreadJobId}" : "";
            var targetStr = !string.IsNullOrEmpty(TargetSite) ? $" → {TargetSite}" : "";
            var progressStr = ProgressPercent.HasValue ? $" ({ProgressPercent}%)" : "";
            var releaseStr = !string.IsNullOrEmpty(ReleaseName) ? $": {ReleaseName}" : "";

            return $"[{Timestamp:yyyy-MM-dd HH:mm:ss}] {prefix}{jobStr}{releaseStr}{targetStr}{progressStr} {Message}";
        }

        private string GetEventPrefix()
        {
            return EventType switch
            {
                CBFTPEventType.Info => "[INFO]",
                CBFTPEventType.Connected => "[CONNECTED]",
                CBFTPEventType.Disconnected => "[DISCONNECTED]",
                CBFTPEventType.SpreadJobSent => "[JOB SENT]",
                CBFTPEventType.SpreadJobStarted => "[JOB STARTED]",
                CBFTPEventType.SpreadJobProgress => "[PROGRESS]",
                CBFTPEventType.SpreadJobCompleted => "[COMPLETED]",
                CBFTPEventType.SpreadJobFailed => "[FAILED]",
                CBFTPEventType.Error => "[ERROR]",
                _ => "[INFO]"
            };
        }

        public Color GetEventColor()
        {
            return EventType switch
            {
                CBFTPEventType.Info => Color.White,
                CBFTPEventType.Connected => Color.LightGreen,
                CBFTPEventType.Disconnected => Color.Orange,
                CBFTPEventType.SpreadJobSent => Color.LightBlue,
                CBFTPEventType.SpreadJobStarted => Color.Yellow,
                CBFTPEventType.SpreadJobCompleted => Color.LightGreen,
                CBFTPEventType.SpreadJobFailed => Color.LightCoral,
                CBFTPEventType.Error => Color.Red,
                _ => Color.White
            };
        }
    }

    #endregion

    #region LogManager Core

    public class LogManagerCore
    {
        // Static initialization is thread-safe; the previous lazy "?? =" pattern could
        // create two instances when first touched from two threads at once.
        private static readonly LogManagerCore _instance = new LogManagerCore();
        public static LogManagerCore Instance => _instance;

        public event EventHandler<LogEntry> LogEntryAdded;

        private List<LogEntry> _allLogs = new List<LogEntry>();
        private readonly object _logLock = new object();
        private int _maxLogEntries = 10000;

        public int MaxLogEntries
        {
            get => _maxLogEntries;
            set => _maxLogEntries = value;
        }

        private LogManagerCore() { }

        // Trim in blocks instead of one entry at a time. Removing a single element
        // from the front of a List is an O(n) memmove of the whole buffer, and it ran
        // inside the global log lock on EVERY log call once the buffer was full —
        // with dozens of log lines per announce that lock is on the race path.
        // Amortising the shift over 10% of the buffer makes it effectively free.
        private const int LogTrimChunkDivisor = 10;

        public void AddLogEntry(LogEntry entry)
        {
            lock (_logLock)
            {
                _allLogs.Add(entry);
                if (_allLogs.Count > _maxLogEntries)
                {
                    int chunk = Math.Max(1, _maxLogEntries / LogTrimChunkDivisor);
                    int excess = _allLogs.Count - _maxLogEntries;
                    int toRemove = Math.Min(_allLogs.Count, Math.Max(excess, chunk));
                    _allLogs.RemoveRange(0, toRemove);
                }
            }

            LogEntryAdded?.Invoke(this, entry);
        }

        public void LogRace(RaceStatus status, string releaseName, string site,
            string targetSite = null, long size = 0, string quality = null,
            string filterReason = null, int? spreadJobId = null)
        {
            var entry = new RaceLogEntry
            {
                Status = status,
                ReleaseName = releaseName,
                Site = site,
                TargetSite = targetSite,
                Size = size,
                Quality = quality,
                FilterReason = filterReason,
                SpreadJobId = spreadJobId,
                Level = status == RaceStatus.Failed ? LogLevel.Error : LogLevel.Info
            };
            // ⭐ USE GetLogColor() instead of GetStatusColor()
            entry.DisplayColor = entry.GetLogColor();
            AddLogEntry(entry);
        }

        public void LogIRC(IRCEventType eventType, string message, string channel = null,
            string server = null, bool ruleMatched = false, string matchedRule = null)
        {
            var entry = new IRCLogEntry
            {
                EventType = eventType,
                Message = message,
                Channel = channel,
                Server = server,
                RuleMatched = ruleMatched,
                MatchedRule = matchedRule,
                Level = eventType == IRCEventType.Error ? LogLevel.Error : LogLevel.Info
            };
            entry.DisplayColor = entry.GetEventColor();
            AddLogEntry(entry);
        }

        public void LogCBFTP(CBFTPEventType eventType, string message, int? spreadJobId = null,
            string releaseName = null, string targetSite = null, int? progressPercent = null)
        {
            var entry = new CBFTPLogEntry
            {
                EventType = eventType,
                Message = message,
                SpreadJobId = spreadJobId,
                ReleaseName = releaseName,
                TargetSite = targetSite,
                ProgressPercent = progressPercent,
                Level = eventType == CBFTPEventType.Error || eventType == CBFTPEventType.SpreadJobFailed
                    ? LogLevel.Error
                    : LogLevel.Info
            };
            entry.DisplayColor = entry.GetEventColor();
            AddLogEntry(entry);
        }

        public void LogApplication(string message, LogLevel level = LogLevel.Info)
        {
            var entry = new LogEntry
            {
                Category = LogCategory.Application,
                Message = message,
                Level = level,
                DisplayColor = GetLevelColor(level)
            };
            AddLogEntry(entry);
        }

        private Color GetLevelColor(LogLevel level)
        {
            return level switch
            {
                LogLevel.Debug => Color.Cyan,
                LogLevel.Info => Color.White,
                LogLevel.Success => Color.LimeGreen,
                LogLevel.Warning => Color.Orange,
                LogLevel.Error => Color.Red,
                LogLevel.Critical => Color.Magenta,
                _ => Color.White
            };
        }

        public List<LogEntry> GetLogs(LogCategory? category = null, DateTime? since = null)
        {
            lock (_logLock)
            {
                var query = _allLogs.AsEnumerable();
                if (category.HasValue)
                    query = query.Where(l => l.Category == category.Value);
                if (since.HasValue)
                    query = query.Where(l => l.Timestamp >= since.Value);
                return query.ToList();
            }
        }

        public void Clear(LogCategory? category = null)
        {
            lock (_logLock)
            {
                if (category.HasValue)
                    _allLogs.RemoveAll(l => l.Category == category.Value);
                else
                    _allLogs.Clear();
            }
        }

        public void ExportToFile(string filePath, LogCategory? category = null)
        {
            var logs = GetLogs(category);
            var lines = logs.Select(l => l.GetFormattedMessage());
            File.WriteAllLines(filePath, lines);
        }
    }

    #endregion

    #region LogManager Static Facade


    public static class LogColors
    {
        // Color tags for RichTextBox
        public static string Cyan(string text) => $"{{CYAN}}{text}{{/CYAN}}";
        public static string Yellow(string text) => $"{{YELLOW}}{text}{{/YELLOW}}";
        public static string Green(string text) => $"{{GREEN}}{text}{{/GREEN}}";
        public static string Red(string text) => $"{{RED}}{text}{{/RED}}";
        public static string Magenta(string text) => $"{{MAGENTA}}{text}{{/MAGENTA}}";
        public static string Orange(string text) => $"{{ORANGE}}{text}{{/ORANGE}}";
        public static string LightBlue(string text) => $"{{LIGHTBLUE}}{text}{{/LIGHTBLUE}}";
    }


    public static class LogManager
    {
        private static ApplicationLog applicationLog;
        private static MainApp mainApp;
        private static IrcLog ircLog;
        private static CBFTPIntegrationLog cbftpLog;
        public static bool DebugEnabled { get; set; } = false;
        private static RaceLog raceLog;
        private static LogManagerCore Core => LogManagerCore.Instance;
        public static bool DisableRaceLog { get; set; } = false;
        public static bool DisableCbftpLog { get; set; } = false;
        public static bool DisableApplicationLog { get; set; } = false;



        public static void Initialize(ApplicationLog app, MainApp main, IrcLog irc, CBFTPIntegrationLog cbftp, RaceLog race)
        {
            applicationLog = app;
            mainApp = main;
            ircLog = irc;
            cbftpLog = cbftp;
            raceLog = race; 

            Core.LogEntryAdded += OnLogEntryAdded;

            Core.LogApplication("LogManager initialized successfully", LogLevel.Info);
        }

        private static void OnLogEntryAdded(object sender, LogEntry entry)
        {


            // UI gating only – IRC is NEVER gated
            if (entry.Category == LogCategory.Race && DisableRaceLog) return;
            if (entry.Category == LogCategory.CBFTP && DisableCbftpLog) return;
            if (entry.Category == LogCategory.Application && DisableApplicationLog) return;



            switch (entry.Category)
            {
                case LogCategory.Race:
                    // ⭐ CHANGED: Send to RaceLog window instead of MainApp
                    if (entry is RaceLogEntry raceEntry)
                        raceLog?.AppendLog(raceEntry.GetFormattedMessage(), raceEntry.DisplayColor);
                    break;

                case LogCategory.IRC:
                    if (entry is IRCLogEntry ircEntry)
                        ircLog?.AppendLog(ircEntry.GetFormattedMessage(), ircEntry.DisplayColor);
                    break;

                case LogCategory.CBFTP:
                    if (entry is CBFTPLogEntry cbftpEntry)
                        cbftpLog?.AppendLog(cbftpEntry.GetFormattedMessage(), cbftpEntry.DisplayColor);
                    break;

                case LogCategory.Application:
                    applicationLog?.AppendLog(entry.GetFormattedMessage(), entry.DisplayColor);
                    break;
            }
        }


        public static void LogCBFTPProgress(int jobId, string message, int? progressPercent = null)
        {
            string formattedMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [PROGRESS] Job#{jobId}: {message}";

            if (cbftpLog != null)
            {
                cbftpLog.UpdateJobProgress(jobId, formattedMessage, Color.Yellow);
            }
        }

        public static void LogCBFTPJobStart(int jobId, string releaseName, string targetSites)
        {
            string message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [JOB STARTED] Job#{jobId}: {releaseName} → {targetSites}";

            cbftpLog?.AddJobProgressEntry(jobId, message, Color.LightBlue);
        }

        public static void LogCBFTPJobComplete(int jobId, string releaseName, CbftpJobStats stats)
        {
            string message = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [COMPLETED] Job#{jobId}: ✓ {stats.FilesTransferred} files | {FormatSize(stats.BytesTransferred)} | Avg: {stats.AverageSpeed:F1} MB/s | Time: {stats.TimeElapsed:mm\\:ss}";

            if (cbftpLog != null)
            {
                cbftpLog.UpdateJobProgress(jobId, message, Color.LightGreen);
                cbftpLog.RemoveJobTracking(jobId);
            }
        }

        private static string FormatSize(long bytes)
        {
            string[] sizes = { "B", "KB", "MB", "GB", "TB" };
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1)
            {
                order++;
                len = len / 1024;
            }
            return $"{len:0.##} {sizes[order]}";
        }


        // Main logging methods
        public static void LogRace(RaceStatus status, string releaseName, string site,
            string targetSite = null, long size = 0, string quality = null,
            string filterReason = null, int? spreadJobId = null)
        {
            if (DisableRaceLog)
                return;

            Core.LogRace(status, releaseName, site, targetSite, size, quality, filterReason, spreadJobId);
        }

        public static void LogIRC(IRCEventType eventType, string message, string channel = null,
            string server = null, bool ruleMatched = false, string matchedRule = null)
        {
            Core.LogIRC(eventType, message, channel, server, ruleMatched, matchedRule);
        }

        public static void LogCBFTP(CBFTPEventType eventType, string message, int? spreadJobId = null,
            string releaseName = null, string targetSite = null, int? progressPercent = null)
        {
            if (DisableCbftpLog)
                return;

            Core.LogCBFTP(eventType, message, spreadJobId, releaseName, targetSite, progressPercent);
        }

        // Simple convenience methods
        public static void Info(string message)
        {
            if (DisableApplicationLog) return;
            Core.LogApplication(message, LogLevel.Info);
        }

        public static void Success(string message)
        {
            if (DisableApplicationLog) return;
            Core.LogApplication(message, LogLevel.Success);
        }

        public static void Warning(string message)
        {
            if (DisableApplicationLog) return;
            Core.LogApplication(message, LogLevel.Warning);
        }

        public static void Error(string message)
        {
            // Errors always reach the core buffer; DisableApplicationLog only
            // suppresses UI display (gated in OnLogEntryAdded).
            Core.LogApplication(message, LogLevel.Error);
        }

        public static void Debug(string message)
        {
            if (DisableApplicationLog) return;
            if (DebugEnabled)
                Core.LogApplication(message, LogLevel.Debug);
        }

        public static void Exception(Exception ex, string context = null)
        {
            // Exceptions always reach the core buffer (see Error).
            var message = context != null ? $"{context}: {ex.Message}" : ex.Message;
            Core.LogApplication(message, LogLevel.Error);
            if (DebugEnabled && ex.StackTrace != null)
                Core.LogApplication($"Stack trace: {ex.StackTrace}", LogLevel.Debug);
        }


    }

    #endregion
}