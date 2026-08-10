/// <summary>
/// Process-wide engine switches.
///
/// These used to be static properties on the WinForms <c>MainApp</c> form, which meant
/// every engine file that wanted to know "is debug on?" had a hard reference to a Form.
/// They are plain settings, so they live here instead.
///
/// Deliberately in the global namespace to match how the ported call sites reference
/// them, and kept as simple statics because they are read on the race hot path where a
/// DI lookup or lock would be wasted work.
/// </summary>
public static class EngineSettings
{
    /// <summary>Verbose diagnostics. Off by default: debug logging is not free on the race path.</summary>
    public static bool DebugEnabled { get; set; }

    /// <summary>
    /// Accept TLS certificates that fail validation when talking to cbftp.
    /// Needed for the self-signed certs most cbftp instances use, but it does mean the
    /// connection is not authenticated — keep it off unless you need it.
    /// </summary>
    public static bool AllowInsecureSsl { get; set; }
}

/// <summary>
/// Console/markup colour helpers used in log strings by the ported engine code.
///
/// In the WinForms build these wrapped text in colour markup for the rich-text log
/// windows. The engine no longer decides how anything is rendered — severity now
/// travels as a <see cref="RaceTrade.Engine.Logging.LogLevel"/> — so these pass the
/// text through unchanged and exist purely so the ported call sites still compile.
/// </summary>
public static class LogColors
{
    // mIRC colour codes. The UI already has an IRC markup renderer for the chat window,
    // so emitting the same encoding here means one converter serves both surfaces and
    // nothing new has to be invented. A consumer that does not render them (a plain
    // console) just sees a stray control byte, never garbled words.
    private const char Colour = '\x03';
    private const char Reset = '\x0F';

    private static string Wrap(string text, int code) =>
        string.IsNullOrEmpty(text) ? text : $"{Colour}{code:00}{text}{Reset}";

    /// <summary>IRC sections and other "this is the thing that matched" values.</summary>
    public static string Green(string text) => Wrap(text, 3);

    /// <summary>Site names.</summary>
    public static string Magenta(string text) => Wrap(text, 6);

    /// <summary>Release names.</summary>
    public static string Orange(string text) => Wrap(text, 7);

    /// <summary>Patterns, rules, mappings.</summary>
    public static string Yellow(string text) => Wrap(text, 8);

    public static string Red(string text) => Wrap(text, 4);

    /// <summary>Bot names, channels, cbftp sections.</summary>
    public static string Cyan(string text) => Wrap(text, 11);
}
