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
    private const string SectionsFile = "sections/cbftp_sections.json";

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
        return cfg;
    }

    public static SiteConfig NewSite(string name = "") => new()
    {
        Server = new ServerSettings { Port = 6697 },
        SiteSettings = new SiteSettings { Sitename = name },
        Sections = new List<Section>(),
        RaceSectionsEnabled = new List<string>(),
        GlobalBlacklist = new List<string>(),
        Affils = new List<string>()
    };

    /// <summary>
    /// Saves the site. Passwords and Blowfish keys are encrypted here if they are still
    /// plaintext, so a value typed into the browser never lands on disk in the clear.
    /// </summary>
    public void Save(SiteConfig cfg, string? originalName = null)
    {
        var server = cfg.Server ?? new ServerSettings();
        var siteSettings = cfg.SiteSettings ?? new SiteSettings();
        cfg.Server = server;
        cfg.SiteSettings = siteSettings;

        var name = siteSettings.Sitename?.Trim();
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("Site name is required.");
        if (IsReservedConfigName(name))
            throw new InvalidOperationException($"'{name}' is a reserved placeholder name and cannot be saved.");

        Directory.CreateDirectory(Dir);

        server.Password = SecureConfig.EncryptIfNeeded(server.Password);

        EncryptChannelKeys(siteSettings);

        AtomicFile.WriteAllText(PathFor(name), JsonConvert.SerializeObject(cfg, Formatting.Indented));

        // Renamed: drop the file under the old name so it does not linger as a duplicate.
        if (!string.IsNullOrWhiteSpace(originalName) &&
            !string.Equals(originalName, name, StringComparison.OrdinalIgnoreCase))
        {
            var old = PathFor(originalName);
            if (File.Exists(old)) File.Delete(old);
        }

        SiteConfigManager.Invalidate();
        RaceHelper.LoadAllSiteConfigs();
        LogManager.Success($"Saved site '{name}'.");
    }

    public void Delete(string name)
    {
        var path = PathFor(name);
        if (File.Exists(path)) File.Delete(path);

        SiteConfigManager.Invalidate();
        RaceHelper.LoadAllSiteConfigs();
        LogManager.Info($"Deleted site '{name}'.");
    }

    public CbftpSiteImportSummary ImportFromCbftpSites(IEnumerable<CbftpSite> sites, bool overwriteExisting)
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

                var path = PathFor(site.Name);
                if (File.Exists(path) && !overwriteExisting)
                {
                    skipped++;
                    messages.Add($"Skipped existing site '{site.Name}'.");
                    continue;
                }

                var siteConfig = new
                {
                    site_settings = new
                    {
                        sitename = site.Name,
                        bot_name = "",
                        disable_site = site.Disabled,
                        pre_announce = "Site",
                        new_regex_pattern = @"\bNEW\b",
                        release_regex_pattern = @"\]\s*(.*?)\s",
                        section_regex_pattern = @"\[(.*?)\]",
                        section_prefix = "[",
                        section_suffix = "]",
                        ignore_words = ""
                    },
                    race_sections_enabled = site.Sections?.Select(s => s.Name).Where(s => !string.IsNullOrWhiteSpace(s)).ToList()
                                            ?? new List<string>(),
                    sections = (site.Sections ?? new List<CbftpSection>())
                        .Where(s => !string.IsNullOrWhiteSpace(s.Name))
                        .Select(s => new
                        {
                            irc_name = s.Name,
                            tags = new[]
                            {
                                new
                                {
                                    map_cbftp_section = s.Name,
                                    trigger_regex = "",
                                    rules = Array.Empty<string>()
                                }
                            },
                            rules = Array.Empty<string>()
                        })
                        .ToArray()
                };

                AtomicFile.WriteAllText(path, JsonConvert.SerializeObject(siteConfig, Formatting.Indented));
                SiteConfigManager.Invalidate(site.Name);

                foreach (var section in site.Sections ?? new List<CbftpSection>())
                {
                    if (!string.IsNullOrWhiteSpace(section.Name))
                        allSections.Add(section.Name);
                }

                LogManager.Success($"Imported site: {site.Name}");
                imported++;
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error importing site {site.Name}: {ex.Message}");
                messages.Add($"Error importing '{site.Name}': {ex.Message}");
                errors++;
            }
        }

        var sectionsAdded = allSections.Count > 0 ? UpdateCbftpSections(allSections) : 0;

        if (imported > 0)
        {
            SiteConfigManager.Invalidate();
            RaceHelper.LoadAllSiteConfigs();
        }

        return new CbftpSiteImportSummary(imported, skipped, errors, sectionsAdded, messages);
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

    private static int UpdateCbftpSections(HashSet<string> newSections)
    {
        Directory.CreateDirectory(SectionsDir);

        SectionData sectionData;
        if (File.Exists(SectionsFile))
        {
            try
            {
                sectionData = JsonConvert.DeserializeObject<SectionData>(File.ReadAllText(SectionsFile))
                              ?? NewSectionData();
            }
            catch (Exception ex)
            {
                LogManager.Warning($"Could not read existing cbftp_sections.json: {ex.Message}");
                sectionData = NewSectionData();
            }
        }
        else
        {
            sectionData = NewSectionData();
        }

        sectionData.Sections ??= new Dictionary<string, string>();
        sectionData.CbftpSections ??= new Dictionary<string, string>();

        var added = 0;
        foreach (var section in newSections.OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
        {
            if (sectionData.CbftpSections.Values.Contains(section, StringComparer.OrdinalIgnoreCase))
                continue;

            var nextId = sectionData.CbftpSections.Count + 1;
            var key = $"cbftp_section{nextId}";
            while (sectionData.CbftpSections.ContainsKey(key))
            {
                nextId++;
                key = $"cbftp_section{nextId}";
            }

            sectionData.CbftpSections[key] = section;
            added++;
        }

        AtomicFile.WriteAllText(SectionsFile, JsonConvert.SerializeObject(sectionData, Formatting.Indented));
        LogManager.Success($"Updated cbftp_sections.json: {added} new section(s) added, {sectionData.CbftpSections.Count} total");
        return added;
    }

    private static SectionData NewSectionData() => new()
    {
        Sections = new Dictionary<string, string>(),
        CbftpSections = new Dictionary<string, string>()
    };

    private static bool IsReservedConfigName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        ReservedConfigNames.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase);

    private static string PathFor(string name) => Path.Combine(Dir, $"{name}.json");
}

public sealed record CbftpSiteImportSummary(
    int Imported,
    int Skipped,
    int Errors,
    int SectionsAdded,
    IReadOnlyList<string> Messages);
