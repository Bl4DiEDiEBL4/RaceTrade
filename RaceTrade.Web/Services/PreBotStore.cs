using Newtonsoft.Json;

namespace RaceTrade.Web.Services;

/// <summary>
/// Reads and writes the PreBot configs under pre_bots\*.json - the web equivalent of the
/// WinForms PreBot form.
/// </summary>
public sealed class PreBotStore
{
    private const string Dir = "pre_bots";

    private static readonly string[] ReservedConfigNames = { "new_prebot", "template", "example" };

    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<string>();

        return Directory.GetFiles(Dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n) && !IsReservedConfigName(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public PreBotConfig Load(string name)
    {
        if (IsReservedConfigName(name))
            throw new InvalidOperationException($"'{name}' is a reserved placeholder name and cannot be loaded.");

        var path = PathFor(name);
        if (!File.Exists(path)) return New(name);

        var cfg = JsonConvert.DeserializeObject<PreBotConfig>(File.ReadAllText(path)) ?? New(name);
        cfg.ZncServer ??= new ZncServerSettings();
        cfg.SiteSettings ??= new PreBotSiteSettings();
        return cfg;
    }

    public static PreBotConfig New(string name = "") => new()
    {
        ZncServer = new ZncServerSettings { Port = 6697 },
        SiteSettings = new PreBotSiteSettings { Sitename = name }
    };

    /// <summary>Saves, encrypting the ZNC password and Blowfish key if still plaintext.</summary>
    public void Save(PreBotConfig cfg, string? originalName = null)
    {
        var znc = cfg.ZncServer ?? new ZncServerSettings();
        var siteSettings = cfg.SiteSettings ?? new PreBotSiteSettings();
        cfg.ZncServer = znc;
        cfg.SiteSettings = siteSettings;

        var name = siteSettings.Sitename?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("PreBot name is required.");
        if (IsReservedConfigName(name))
            throw new InvalidOperationException($"'{name}' is a reserved placeholder name and cannot be saved.");

        Directory.CreateDirectory(Dir);

        znc.Password = SecureConfig.EncryptIfNeeded(znc.Password);
        siteSettings.BlowfishKey1 = SecureConfig.EncryptIfNeeded(siteSettings.BlowfishKey1);

        AtomicFile.WriteAllText(PathFor(name), JsonConvert.SerializeObject(cfg, Formatting.Indented));

        if (!string.IsNullOrWhiteSpace(originalName) &&
            !string.Equals(originalName, name, StringComparison.OrdinalIgnoreCase))
        {
            var old = PathFor(originalName);
            if (File.Exists(old)) File.Delete(old);
        }

        LogManager.Success($"Saved prebot '{name}'.");
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);
        LogManager.Info($"Deleted prebot '{name}'.");
    }

    private static bool IsReservedConfigName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        ReservedConfigNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string PathFor(string name) => Path.Combine(Dir, $"{name}.json");
}
