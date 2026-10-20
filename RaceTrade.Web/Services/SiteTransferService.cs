using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RaceTrade.Web.Services;

public sealed class SiteTransferService(SiteStore store)
{
    public const long MaxFileBytes = 5 * 1024 * 1024;
    private const string Format = "RaceTrade-sites";

    public string Export(IEnumerable<string> keys, bool includeSecrets)
    {
        var available = store.ListNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configs = new List<SiteConfig>();
        foreach (var key in keys.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!available.Contains(key))
                throw new InvalidOperationException("The selected site no longer exists.");
            var config = store.Load(key);
            TransformSecrets(config, value => includeSecrets ? SecureConfig.Decrypt(value) : "");
            if (!includeSecrets) config.Server.Username = "";
            configs.Add(config);
        }
        if (configs.Count == 0) throw new InvalidOperationException("No sites selected.");
        return JsonConvert.SerializeObject(new
        {
            format = Format, version = 1, includes_secrets = includeSecrets, sites = configs
        }, Formatting.Indented);
    }

    public IReadOnlyList<SiteConfig> Parse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > MaxFileBytes)
            throw new InvalidOperationException("The import file must be 5 MB or smaller.");
        using var text = new StringReader(json);
        using var reader = new JsonTextReader(text) { MaxDepth = 64, DateParseHandling = DateParseHandling.None };
        var root = JToken.Load(reader, new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        if (reader.Read()) throw new InvalidOperationException("The file contains more than one JSON document.");
        JArray sites;
        if (root is JObject bundle && bundle["format"] is not null)
        {
            if (bundle.Value<string>("format") != Format || bundle.Value<int?>("version") != 1)
                throw new InvalidOperationException("Unsupported site export format or version.");
            sites = bundle["sites"] as JArray ?? throw new InvalidOperationException("The export has no site list.");
        }
        else if (root is JObject single && single["site_settings"] is JObject)
        {
            sites = new JArray(single);
        }
        else throw new InvalidOperationException("Choose a RaceTrade site export or site configuration JSON file.");

        if (sites.Count == 0 || sites.Count > 1000)
            throw new InvalidOperationException("The import must contain between 1 and 1000 sites.");
        var serializer = JsonSerializer.Create(new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.None });
        var configs = new List<SiteConfig>();
        foreach (var item in sites)
        {
            if (item is not JObject obj || obj["site_settings"] is not JObject)
                throw new InvalidOperationException("A site is missing its site settings.");
            var config = obj.ToObject<SiteConfig>(serializer) ?? throw new InvalidOperationException("Invalid site configuration.");
            ValidateName(config.SiteSettings.Sitename);
            config.Server ??= new ServerSettings { Port = 6697 };
            config.Sections ??= new();
            if (config.Sections.Any(s => s is null || s.Tags?.Any(t => t is null) == true))
                throw new InvalidOperationException("A site contains an invalid section or mapping.");
            configs.Add(config);
        }
        return configs;
    }

    public SiteFileImportSummary Import(IEnumerable<SiteConfig> sites, string backendId, bool overwrite, bool disable)
    {
        // Prepare the complete selection before saving, so invalid names or unreadable
        // machine-bound secrets cannot leave a half-imported selection behind.
        var prepared = sites.Select(s => JsonConvert.DeserializeObject<SiteConfig>(JsonConvert.SerializeObject(s))!).ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var config in prepared)
        {
            var settings = config.SiteSettings ?? throw new InvalidOperationException("A site is missing its site settings.");
            ValidateName(settings.Sitename);
            settings.Sitename = settings.Sitename.Trim();
            if (!names.Add(config.SiteSettings.Sitename))
                throw new InvalidOperationException("Select only one configuration for each site name on the destination backend.");
            config.Server ??= new ServerSettings { Port = 6697 };
            config.SiteSettings.FxpBackendId = backendId;
            config.SiteSettings.ConfigKey = null;
            if (disable) config.SiteSettings.DisableSite = true;
            TransformSecrets(config, value => SecureConfig.EncryptIfNeeded(SecureConfig.Decrypt(value)));
        }
        if (prepared.Count == 0) throw new InvalidOperationException("Select at least one site to import.");
        if (!disable && string.IsNullOrWhiteSpace(backendId))
            throw new InvalidOperationException("Choose an FXP backend before importing enabled sites.");

        var existing = store.ListNames().Select(key => (Key: key, Config: store.Load(key))).ToList();
        var imported = 0;
        var skipped = 0;
        var errors = new List<string>();
        foreach (var config in prepared)
        {
            var match = existing.FirstOrDefault(e =>
                string.Equals(e.Config.SiteSettings.Sitename, config.SiteSettings.Sitename, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.Config.SiteSettings.FxpBackendId ?? "", backendId, StringComparison.OrdinalIgnoreCase));
            if (match.Key is not null && !overwrite) { skipped++; continue; }
            try
            {
                if (match.Key is not null) PreserveMissingSecrets(config, match.Config);
                store.Save(config, match.Key, refreshEngine: false);
                imported++;
            }
            catch (Exception)
            {
                errors.Add($"Could not save site '{config.SiteSettings.Sitename}'. Check the data folder permissions.");
            }
        }
        if (imported > 0)
        {
            SiteConfigManager.Invalidate();
            RaceHelper.LoadAllSiteConfigs();
        }
        LogManager.Info($"Site file import: {imported} imported, {skipped} skipped, {errors.Count} failed.");
        return new SiteFileImportSummary(imported, skipped, errors);
    }

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 ||
            name.Any(c => char.IsControl(c) || "<>:\"/\\|?*".Contains(c)) ||
            new[] { "new_site", "template", "example" }.Contains(name.Trim(), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException("A site has an invalid or reserved name.");
    }

    private static void TransformSecrets(SiteConfig config, Func<string, string> transform)
    {
        config.Server.Password = transform(config.Server.Password);
        foreach (var row in SiteStore.ReadChannels(config.SiteSettings))
            config.SiteSettings.GetType().GetProperty($"BlowfishKey{row.Index}")!.SetValue(config.SiteSettings, transform(row.Key));
        if (config.SiteSettings.ChatKeys is not null)
            foreach (var channel in config.SiteSettings.ChatKeys.Keys.ToList())
                config.SiteSettings.ChatKeys[channel] = transform(config.SiteSettings.ChatKeys[channel]);
    }

    private static void PreserveMissingSecrets(SiteConfig target, SiteConfig existing)
    {
        if (string.IsNullOrEmpty(target.Server.Username)) target.Server.Username = existing.Server.Username;
        if (string.IsNullOrEmpty(target.Server.Password)) target.Server.Password = existing.Server.Password;
        foreach (var row in SiteStore.ReadChannels(target.SiteSettings))
        {
            if (!string.IsNullOrEmpty(row.Key) || string.IsNullOrWhiteSpace(row.Channel)) continue;
            var old = SiteStore.ReadChannels(existing.SiteSettings).FirstOrDefault(c =>
                string.Equals(c.Channel, row.Channel, StringComparison.OrdinalIgnoreCase));
            target.SiteSettings.GetType().GetProperty($"BlowfishKey{row.Index}")!.SetValue(target.SiteSettings, old.Key);
        }
        target.SiteSettings.ChatKeys ??= new();
        foreach (var channel in target.SiteSettings.ChatKeys.Keys.ToList())
            if (string.IsNullOrEmpty(target.SiteSettings.ChatKeys[channel]) &&
                existing.SiteSettings.ChatKeys?.TryGetValue(channel, out var key) == true)
                target.SiteSettings.ChatKeys[channel] = key;
    }
}

public sealed record SiteFileImportSummary(int Imported, int Skipped, IReadOnlyList<string> Errors);
