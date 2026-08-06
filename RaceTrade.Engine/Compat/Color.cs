using RaceTrade.Engine.Logging;

namespace RaceTrade.Engine.Compat
{
    /// <summary>
    /// Compatibility shim for the ported engine code.
    ///
    /// The WinForms engine expressed the severity of a message by passing a
    /// System.Drawing.Color to its output callbacks (AppendOutput(msg, Color.Red)).
    /// System.Drawing.Common is a Windows-only package on .NET 8, so keeping it would
    /// have defeated the whole point of a headless engine — but rewriting several
    /// hundred call sites at once would have been a large, risky diff.
    ///
    /// So this is a tiny value type with the same member names, carrying a
    /// <see cref="LogLevel"/> instead of an RGB value. Existing call sites compile
    /// unchanged, and anything that consumes the callback gets real severity via
    /// <see cref="Level"/> rather than having to guess from a colour.
    ///
    /// New engine code should take ILogSink and use LogLevel directly; this type
    /// exists to let the port land in one piece and can be retired call site by
    /// call site.
    /// </summary>
    public readonly struct Color
    {
        public string Name { get; }
        public LogLevel Level { get; }

        private Color(string name, LogLevel level)
        {
            Name = name;
            Level = level;
        }

        // Severity-carrying colours as used by the original call sites.
        public static Color Red => new Color(nameof(Red), LogLevel.Error);
        public static Color Orange => new Color(nameof(Orange), LogLevel.Warning);
        public static Color Yellow => new Color(nameof(Yellow), LogLevel.Warning);
        public static Color Green => new Color(nameof(Green), LogLevel.Success);
        public static Color LightBlue => new Color(nameof(LightBlue), LogLevel.Info);
        public static Color Cyan => new Color(nameof(Cyan), LogLevel.Info);
        public static Color White => new Color(nameof(White), LogLevel.Info);
        public static Color Magenta => new Color(nameof(Magenta), LogLevel.Info);
        public static Color Black => new Color(nameof(Black), LogLevel.Info);
        public static Color Gray => new Color(nameof(Gray), LogLevel.Debug);
        public static Color DimGray => new Color(nameof(DimGray), LogLevel.Debug);

        public override string ToString() => Name;
    }
}
