using Newtonsoft.Json.Linq;
using RaceTrade;

namespace RaceTrade.Web.Services;

public sealed class ThemeService
{
    private const string SettingsFile = "settings/settings.json";
    private readonly object _lock = new();
    private string? _currentTheme;

    public string CurrentTheme
    {
        get
        {
            lock (_lock)
            {
                return _currentTheme ??= LoadTheme();
            }
        }
    }

    public bool IsLight => string.Equals(CurrentTheme, "light", StringComparison.OrdinalIgnoreCase);

    public string SetTheme(string theme)
    {
        var normalized = Normalize(theme);

        lock (_lock)
        {
            Directory.CreateDirectory("settings");

            var o = File.Exists(SettingsFile)
                ? JObject.Parse(File.ReadAllText(SettingsFile))
                : new JObject();

            o["theme"] = normalized;
            AtomicFile.WriteAllText(SettingsFile, o.ToString(Newtonsoft.Json.Formatting.Indented));
            _currentTheme = normalized;
            return normalized;
        }
    }

    private static string LoadTheme()
    {
        try
        {
            if (!File.Exists(SettingsFile))
                return "dark";

            var o = JObject.Parse(File.ReadAllText(SettingsFile));
            var theme = (string?)o["theme"] ?? (string?)o["Theme"];
            return Normalize(theme);
        }
        catch
        {
            return "dark";
        }
    }

    private static string Normalize(string? theme) =>
        string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase) ? "light" : "dark";
}
