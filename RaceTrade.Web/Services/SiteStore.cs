using Newtonsoft.Json;

namespace RaceTrade.Web.Services;

/// <summary>
/// Reads and writes the site configs under sites\*.json - the web equivalent of the
/// WinForms AddSite form's load/save logic.
///
/// Writes go through <see cref="AtomicFile"/> (temp file + replace) so a crash mid-save
/// cannot leave a half-written config, and every save invalidates
/// <see cref="SiteConfigManager"/> so the running racer picks the change up immediately
/// instead of after a restart.
/// </summary>
public sealed class SiteStore
{
    private const string Dir = "sites";
    private const string SectionsDir = "sections";
    private static readonly string SectionsFile = Path.Combine(SectionsDir, "fxp_backend_sections.json");
    private static readonly string LegacySectionsFile = Path.Combine(SectionsDir, "c" + "bftp_sections.json");

    private static readonly string[] ReservedConfigNames = { "new_site", "template", "example" };

    public IReadOnlyList<string> ListNames()
    {
        if (!Directory.Exists(Dir)) return Array.Empty<string>();

        return Directory.GetFiles(Dir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => !string.IsNullOrEmpty(n) && !IsReservedConfigName(n))
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public SiteConfig Load(string name)
    {
        if (IsReservedConfigName(name))
            throw new InvalidOperationException($"'{name}' is a reserved placeholder name and cannot be loaded.");

        var path = PathFor(name);
        if (!File.Exists(path)) return NewSite(name);

        var cfg = JsonConvert.DeserializeObject<SiteConfig>(File.ReadAllText(path)) ?? NewSite(name);
        cfg.Server ??= new ServerSettings();
        cfg.SiteSettings ??= new SiteSettings();
        cfg.SiteSettings.ConfigKey = name;
        if (string.IsNullOrWhiteSpace(cfg.SiteSettings.PreOrSite)) cfg.SiteSettings.PreOrSite = "Site";
        NormalizeRaceSectionsEnabled(cfg);
        return cfg;
    }

    public static SiteConfig NewSite(string name = "") => new()
    {
        Server = new ServerSettings { Port = 6697 },
        SiteSettings = new SiteSettings
        {
            Sitename = name,
            PreOrSite = "Site",
            IncompleteMarkerRegex = @"WARN:\s+AUTONUKE\s+INCOMPLETE",
            IncompleteSectionRegex = @"INCOMPLETE\s+\[([^\]]+)\]",
            IncompleteReleaseRegex = @"INCOMPLETE\s+\[[^\]]+\]\s+(\S+)",
            IncompleteSectionPrefix = "[",
            IncompleteSectionSuffix = "]",
            IncompleteSearchCommandTemplate = "SITE SEARCH {release}",
            IncompleteDstPathTemplate = "/{section}"
        },
        Sections = new List<Section>(),
        RaceSectionsEnabled = new List<string>(),
        GlobalBlacklist = new List<string>(),
        Affils = new List<string>()
    };

    /// <summary>
    /// Saves the site. Passwords and Blowfish keys are encrypted here if they are still
    /// plaintext, so a value typed into the browser never lands on disk in the clear.
    /// </summary>
    public string Save(SiteConfig cfg, string? originalName = null)
    {
        var server = cfg.Server ?? new ServerSettings();
        var siteSettings = cfg.SiteSettings ?? new SiteSettings();
        cfg.Server = server;
        cfg.SiteSettings = siteSettings;
        NormalizeRaceSectionsEnabled(cfg);

        var name = siteSettings.Sitename?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Site name is required.");
        if (IsReservedConfigName(name))
            throw new InvalidOperationException($"'{name}' is a reserved placeholder name and cannot be saved.");

        Directory.CreateDirectory(Dir);

        server.Password = SecureConfig.EncryptIfNeeded(server.Password);

        EncryptChannelKeys(siteSettings);

        var configKey = ResolveSaveKey(name, siteSettings.FxpBackendId, originalName);
        siteSettings.ConfigKey = configKey;

        AtomicFile.WriteAllText(PathFor(configKey), JsonConvert.SerializeObject(cfg, Formatting.Indented));

        // Renamed: drop the file under the old name so it does not linger as a duplicate.
        if (!string.IsNullOrWhiteSpace(originalName) &&
            !string.Equals(originalName, configKey, StringComparison.OrdinalIgnoreCase))
        {
            var old = PathFor(originalName);
            if (File.Exists(old)) File.Delete(old);
        }

        SiteConfigManager.Invalidate(originalName);
        SiteConfigManager.Invalidate(configKey);
        RaceHelper.LoadAllSiteConfigs();
        LogManager.Success($"Saved site '{name}' as config '{configKey}'.");
        return configKey;
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);

        SiteConfigManager.Invalidate();
        RaceHelper.LoadAllSiteConfigs();
        LogManager.Info($"Deleted site '{name}'.");
    }

    public SiteConfig Duplicate(string sourceName, string newName)
    {
        var targetName = newName?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(sourceName))
            throw new InvalidOperationException("Source site is required.");
        if (string.IsNullOrWhiteSpace(targetName))
            throw new InvalidOperationException("New site name is required.");
        if (IsReservedConfigName(targetName))
            throw new InvalidOperationException($"'{targetName}' is a reserved placeholder name and cannot be saved.");

        Directory.CreateDirectory(Dir);
        if (File.Exists(PathFor(targetName)))
            throw new InvalidOperationException($"Site '{targetName}' already exists.");

        var cfg = Load(sourceName);
        cfg.SiteSettings ??= new SiteSettings();
        cfg.SiteSettings.Sitename = targetName;

        Save(cfg);
        LogManager.Info($"Duplicated site '{sourceName}' as '{targetName}'.");
        return cfg;
    }

    public FxpBackendSiteImportSummary ImportFromFxpBackendSites(IEnumerable<FxpBackendSite> sites, bool overwriteExisting, string? fxpBackendServerId = null)
    {
        Directory.CreateDirectory(Dir);

        var imported = 0;
        var skipped = 0;
        var errors = 0;
        var allSections = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var messages = new List<string>();

        foreach (var site in sites)
        {
            if (site == null || string.IsNullOrWhiteSpace(site.Name))
            {
                skipped++;
                continue;
            }

            try
            {
                if (IsReservedConfigName(site.Name))
                {
                    skipped++;
                    messages.Add($"Skipped reserved placeholder site name '{site.Name}'.");
                    continue;
                }

                var existingKey = FindConfigKey(site.Name, fxpBackendServerId);
                var configKey = existingKey is null
                    ? ResolveNewKey(site.Name, fxpBackendServerId)
                    : ResolveSaveKey(site.Name, fxpBackendServerId, existingKey);
                var path = PathFor(configKey);
                if (existingKey is not null && !overwriteExisting)
                {
                    skipped++;
                    messages.Add($"Skipped existing site '{site.Name}' for FXP backend '{DisplayBackendId(fxpBackendServerId)}'.");
                    continue;
                }

                var siteConfig = new
                {
                    site_settings = new
                    {
                        sitename = site.Name,
                        fxp_backend_id = fxpBackendServerId ?? "",
                        bot_name = "",
                        disable_site = site.Disabled,
                        pre_announce = "Site",
                        new_regex_pattern = @"\bNEW\b",
                        release_regex_pattern = @"\]\s*(.*?)\s",
                        section_regex_pattern = @"\[(.*?)\]",
                        section_prefix = "[",
                        section_suffix = "]",
                        ignore_words = "",
                        incomplete_marker_regex = @"WARN:\s+AUTONUKE\s+INCOMPLETE",
                        incomplete_section_regex = @"INCOMPLETE\s+\[([^\]]+)\]",
                        incomplete_release_regex = @"INCOMPLETE\s+\[[^\]]+\]\s+(\S+)",
                        incomplete_section_prefix = "[",
                        incomplete_section_suffix = "]",
                        incomplete_search_command_template = "SITE SEARCH {release}",
                        incomplete_dst_path_template = "/{section}"
                    },
                    race_sections_enabled = site.Sections?
                                            .Select(s => (s.Name ?? "").Trim())
                                            .Where(s => !string.IsNullOrWhiteSpace(s))
                                            .Distinct(StringComparer.OrdinalIgnoreCase)
                                            .ToList()
                                            ?? new List<string>(),
                    affils = (site.Affils ?? new List<string>())
                             .Where(a => !string.IsNullOrWhiteSpace(a))
                             .Select(a => a.Trim())
                             .Distinct(StringComparer.OrdinalIgnoreCase)
                             .ToList(),
                    sections = (site.Sections ?? new List<FxpBackendSection>())
                        .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                        .Select(s => new
                        {
                            irc_name = s.Name,
                            tags = new[]
                            {
                                new
                                {
                                    map_fxp_backend_section = s.Name,
                                    trigger_regex = "",
                                    rules = Array.Empty<string>()
                                }
                            },
                            rules = Array.Empty<string>()
                        })
                        .ToArray()
                };

                AtomicFile.WriteAllText(path, JsonConvert.SerializeObject(siteConfig, Formatting.Indented));
                if (!string.IsNullOrWhiteSpace(existingKey) &&
                    !string.Equals(existingKey, configKey, StringComparison.OrdinalIgnoreCase))
                {
                    var oldPath = PathFor(existingKey);
                    if (File.Exists(oldPath))
                        File.Delete(oldPath);

                    SiteConfigManager.Invalidate(existingKey);
                }

                SiteConfigManager.Invalidate(configKey);

                foreach (var section in site.Sections ?? new List<FxpBackendSection>())
                {
                    if (!string.IsNullOrWhiteSpace(section.Name))
                        allSections.Add(section.Name);
                }

                LogManager.Success($"Imported site: {site.Name} ({configKey})");
                imported++;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error importing site {site.Name}: {ex.Message}");
                messages.Add($"Error importing '{site.Name}': {ex.Message}");
                errors++;
            }
        }

        var sectionsAdded = allSections.Count > 0 ? UpdateFxpBackendSections(allSections) : 0;

        if (imported > 0)
        {
            SiteConfigManager.Invalidate();
            RaceHelper.LoadAllSiteConfigs();
        }

        return new FxpBackendSiteImportSummary(imported, skipped, errors, sectionsAdded, messages);
    }

    /// <summary>Channel + key pairs (Chan1..Chan20 / BlowfishKey1..20) as an editable list.</summary>
    public static List<(int Index, string Channel, string Key)> ReadChannels(SiteSettings s)
    {
        var list = new List<(int, string, string)>();
        for (var i = 1; i <= 20; i++)
        {
            var chan = s.GetType().GetProperty($"Chan{i}")?.GetValue(s) as string;
            var key = s.GetType().GetProperty($"BlowfishKey{i}")?.GetValue(s) as string;
            list.Add((i, chan ?? "", key ?? ""));
        }
        return list;
    }

    public static void WriteChannels(SiteSettings s, IEnumerable<(int Index, string Channel, string Key)> rows)
    {
        foreach (var (i, chan, key) in rows)
        {
            s.GetType().GetProperty($"Chan{i}")?.SetValue(s, string.IsNullOrWhiteSpace(chan) ? null : chan.Trim());
            s.GetType().GetProperty($"BlowfishKey{i}")?.SetValue(s, string.IsNullOrWhiteSpace(key) ? null : key);
        }
    }

    private static void EncryptChannelKeys(SiteSettings s)
    {
        if (s == null) return;

        for (var i = 1; i <= 20; i++)
        {
            var prop = s.GetType().GetProperty($"BlowfishKey{i}");
            if (prop?.GetValue(s) is string k && !string.IsNullOrWhiteSpace(k))
                prop.SetValue(s, SecureConfig.EncryptIfNeeded(k));
        }
    }

    private static int UpdateFxpBackendSections(HashSet<string> newSections)
    {
        Directory.CreateDirectory(SectionsDir);

        SectionData sectionData;
        var readableSectionsFile = File.Exists(SectionsFile) ? SectionsFile : LegacySectionsFile;
        if (File.Exists(readableSectionsFile))
        {
            try
            {
                sectionData = JsonConvert.DeserializeObject<SectionData>(File.ReadAllText(readableSectionsFile))
                              ?? NewSectionData();
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Could not read existing section mapping file: {ex.Message}");
                sectionData = NewSectionData();
            }
        }
        else
        {
            sectionData = NewSectionData();
        }

        sectionData.Sections ??= new Dictionary<string, string>();
        sectionData.FxpBackendSections ??= new Dictionary<string, string>();

        var added = 0;
        foreach (var section in newSections.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            if (sectionData.FxpBackendSections.Values.Contains(section, StringComparer.OrdinalIgnoreCase))
                continue;

            var nextId = sectionData.FxpBackendSections.Count + 1;
            var key = $"fxp_backend_section{nextId}";
            while (sectionData.FxpBackendSections.ContainsKey(key))
            {
                nextId++;
                key = $"fxp_backend_section{nextId}";
            }

            sectionData.FxpBackendSections[key] = section;
            added++;
        }

        AtomicFile.WriteAllText(SectionsFile, JsonConvert.SerializeObject(sectionData, Formatting.Indented));
        LogManager.Success($"Updated fxp_backend_sections.json: {added} new section(s) added, {sectionData.FxpBackendSections.Count} total");
        return added;
    }

    private static SectionData NewSectionData() => new()
    {
        Sections = new Dictionary<string, string>(),
        FxpBackendSections = new Dictionary<string, string>()
    };

    private static bool IsReservedConfigName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        ReservedConfigNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string PathFor(string name) => Path.Combine(Dir, $"{name}.json");

    private static string RemoteName(SiteConfig cfg, string fallbackKey) =>
        cfg.SiteSettings?.Sitename?.Trim() is { Length: > 0 } name ? name : fallbackKey;

    private static string BackendId(SiteConfig cfg) =>
        cfg.SiteSettings?.FxpBackendId?.Trim() ?? "";

    private static void NormalizeRaceSectionsEnabled(SiteConfig cfg)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();

        foreach (var section in cfg.RaceSectionsEnabled ?? new List<string>())
        {
            var name = section?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (seen.Add(name))
                normalized.Add(name);
        }

        cfg.RaceSectionsEnabled = normalized;
    }

    private static string DisplayBackendId(string? backendId) =>
        string.IsNullOrWhiteSpace(backendId) ? "default" : backendId.Trim();

    private static string ResolveSaveKey(string siteName, string? backendId, string? originalName)
    {
        var preferred = PreferredConfigKey(siteName, backendId);
        if (string.IsNullOrWhiteSpace(originalName))
            return ResolveAvailableKey(preferred, null);

        var original = originalName.Trim();
        if (string.Equals(original, preferred, StringComparison.OrdinalIgnoreCase))
            return original;

        return ResolveAvailableKey(preferred, original);
    }

    private static string ResolveNewKey(string siteName, string? backendId)
    {
        var preferred = PreferredConfigKey(siteName, backendId);
        return ResolveAvailableKey(preferred, null);
    }

    private static string PreferredConfigKey(string siteName, string? backendId)
    {
        var sitePart = SafeFileName(siteName);
        if (string.IsNullOrWhiteSpace(sitePart))
            sitePart = "site";

        var backendPart = SafeFileName(backendId);
        return string.IsNullOrWhiteSpace(backendPart)
            ? sitePart
            : $"{backendPart}__{sitePart}";
    }

    private static string ResolveAvailableKey(string preferred, string? originalName)
    {
        if (!File.Exists(PathFor(preferred)) ||
            string.Equals(preferred, originalName, StringComparison.OrdinalIgnoreCase))
            return preferred;

        for (var i = 2; ; i++)
        {
            var candidate = $"{preferred}_{i}";
            if (!File.Exists(PathFor(candidate)) ||
                string.Equals(candidate, originalName, StringComparison.OrdinalIgnoreCase))
                return candidate;
        }
    }

    private string? FindConfigKey(string siteName, string? backendId)
    {
        foreach (var key in ListNames())
        {
            SiteConfig cfg;
            try
            {
                cfg = Load(key);
            }
            catch
            {
                continue;
            }

            if (!string.Equals(RemoteName(cfg, key), siteName, StringComparison.OrdinalIgnoreCase))
                continue;

            if (string.Equals(BackendId(cfg), backendId?.Trim() ?? "", StringComparison.OrdinalIgnoreCase))
                return key;
        }

        return null;
    }

    private static string SafeFileName(string? value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0)
            return "";

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var chars = text
            .Select(ch => invalid.Contains(ch) || char.IsWhiteSpace(ch) ? '_' : ch)
            .ToArray();
        return new string(chars).Trim('_');
    }
}

public sealed record FxpBackendSiteImportSummary(
    int Imported,
    int Skipped,
    int Errors,
    int SectionsAdded,
    IReadOnlyList<string> Messages);
