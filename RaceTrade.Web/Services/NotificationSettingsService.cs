using Newtonsoft.Json.Linq;

namespace RaceTrade.Web.Services;

public sealed class NotificationSettingsService
{
    private const string SettingsFile = "settings/settings.json";

    private readonly object _gate = new();
    private NotificationSettings _current;

    public NotificationSettingsService()
    {
        _current = LoadFromDisk();
    }

    public event Action? Changed;

    public NotificationSettings Current
    {
        get
        {
            lock (_gate) return _current;
        }
    }

    public NotificationSettings Reload()
    {
        var loaded = LoadFromDisk();

        lock (_gate) _current = loaded;

        Changed?.Invoke();
        return loaded;
    }

    public void Save(NotificationSettings settings)
    {
        Directory.CreateDirectory("settings");

        var o = File.Exists(SettingsFile)
            ? JObject.Parse(File.ReadAllText(SettingsFile))
            : new JObject();

        o["tray_icon_enabled"] = settings.TrayIconEnabled;
        o["race_notifications_enabled"] = settings.RaceNotificationsEnabled;

        AtomicFile.WriteAllText(SettingsFile, o.ToString(Newtonsoft.Json.Formatting.Indented));

        lock (_gate) _current = settings;

        Changed?.Invoke();
    }

    private static NotificationSettings LoadFromDisk()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return NotificationSettings.Default;

            var o = JObject.Parse(File.ReadAllText(SettingsFile));

            return new NotificationSettings(
                ReadBool(o, "tray_icon_enabled", "TrayIconEnabled") ?? true,
                ReadBool(o, "race_notifications_enabled", "RaceNotificationsEnabled") ?? true);
        }
        catch
        {
            return NotificationSettings.Default;
        }
    }

    private static bool? ReadBool(JObject o, params string[] keys)
    {
        foreach (var key in keys)
        {
            var token = o[key];
            if (token is null) continue;

            if (token.Type == JTokenType.Boolean)
                return token.Value<bool>();

            if (bool.TryParse(token.ToString(), out var value))
                return value;
        }

        return null;
    }
}

public sealed record NotificationSettings(
    bool TrayIconEnabled,
    bool RaceNotificationsEnabled)
{
    public static NotificationSettings Default { get; } = new(true, true);
}
