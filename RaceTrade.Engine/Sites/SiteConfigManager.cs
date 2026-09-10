using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaceTrade.Engine.Logging;
using RaceTrade;

/// <summary>
/// Centralized manager for loading and caching site configurations.
/// Used by FtpClientForm for on-demand site config loading.
/// </summary>
public static class SiteConfigManager
{
    private static readonly ConcurrentDictionary<string, SiteConfig> ConfigCache = new();
    private static readonly object cacheLock = new object();
    private static readonly JsonSerializerSettings TolerantDeserializeSettings = new()
    {
        Error = (_, args) =>
        {
            var path = string.IsNullOrWhiteSpace(args.ErrorContext.Path)
                ? "unknown path"
                : args.ErrorContext.Path;
            Console.WriteLine($"[SiteConfigManager] Ignoring invalid site config value at '{path}': {args.ErrorContext.Error.Message}");
            args.ErrorContext.Handled = true;
        }
    };

    // Template/placeholder files to ignore
    private static readonly string[] IgnoredSiteNames = { "new_site", "template", "example" };

    /// <summary>
    /// Checks if a site name should be ignored (template/placeholder files).
    /// </summary>
    private static bool ShouldIgnoreSite(string siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName))
            return true;

        var lowerName = siteName.ToLowerInvariant();
        foreach (var ignored in IgnoredSiteNames)
        {
            if (lowerName == ignored)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Gets a site configuration, loading from file if not cached.
    /// </summary>
    /// <param name="siteName">Name of the site (without .json extension)</param>
    /// <returns>The site configuration</returns>
    /// <exception cref="FileNotFoundException">If the site config file doesn't exist</exception>
    /// <exception cref="InvalidOperationException">If deserialization fails</exception>
    public static SiteConfig GetSiteConfig(string siteName)
    {
        if (string.IsNullOrWhiteSpace(siteName))
        {
            throw new ArgumentException("Site name cannot be null or empty", nameof(siteName));
        }

        if (ShouldIgnoreSite(siteName))
        {
            throw new InvalidOperationException($"Site '{siteName}' is a template/placeholder and cannot be loaded");
        }

        // Try to get from cache first (fast path)
        if (ConfigCache.TryGetValue(siteName, out var config))
        {
            return config;
        }

        // Not in cache, load it (slow path with locking)
        return LoadAndCacheSiteConfig(siteName);
    }

    /// <summary>
    /// Loads a site config from disk and caches it.
    /// Uses double-check locking pattern for thread safety.
    /// </summary>
    private static SiteConfig LoadAndCacheSiteConfig(string siteName)
    {
        lock (cacheLock)
        {
            // Double-check: another thread might have loaded it while we waited
            if (ConfigCache.TryGetValue(siteName, out var config))
            {
                return config;
            }

            var filePath = ResolveSitePath(siteName, out var configKey);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Site configuration file not found: {filePath}", filePath);
            }

            try
            {
                var json = File.ReadAllText(filePath);
                // Legacy/hand-edited configs can contain values such as "" where a
                // newer model expects int/bool/object. RaceHelper's JObject loader has
                // always tolerated that; keep the typed loader equally forgiving so
                // Start does not reject otherwise usable site files.
                config = JsonConvert.DeserializeObject<SiteConfig>(json, TolerantDeserializeSettings);

                if (config == null)
                {
                    throw new InvalidOperationException($"Failed to deserialize site configuration: {filePath}");
                }

                // Validate required fields
                if (config.SiteSettings == null)
                {
                    throw new InvalidOperationException($"Site configuration missing 'site_settings': {filePath}");
                }

                config.SiteSettings.ConfigKey = configKey;
                NormalizeRaceSectionsEnabled(config);

                // Cache it
                ConfigCache[configKey] = config;
                if (!string.Equals(configKey, siteName, StringComparison.OrdinalIgnoreCase))
                    ConfigCache[siteName] = config;

                Console.WriteLine($"[SiteConfigManager] Loaded and cached configuration for '{configKey}'");

                return config;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid JSON in site configuration: {filePath}", ex);
            }
        }
    }

    private static string ResolveSitePath(string siteName, out string configKey)
    {
        configKey = siteName;
        var directPath = Path.Combine("sites", $"{siteName}.json");
        if (File.Exists(directPath))
            return directPath;

        if (!Directory.Exists("sites"))
            return directPath;

        var matches = new List<(string Key, string Path)>();
        foreach (var filePath in Directory.GetFiles("sites", "*.json"))
        {
            var key = Path.GetFileNameWithoutExtension(filePath);
            if (ShouldIgnoreSite(key))
                continue;

            try
            {
                var root = JObject.Parse(File.ReadAllText(filePath));
                var remoteName = root["site_settings"]?["sitename"]?.ToString()?.Trim();
                if (string.Equals(remoteName, siteName, StringComparison.OrdinalIgnoreCase))
                    matches.Add((key, filePath));
            }
            catch
            {
                // Let the normal load path report parse errors for direct matches. The
                // fallback scan should not make one bad site block every other lookup.
            }
        }

        if (matches.Count == 1)
        {
            configKey = matches[0].Key;
            return matches[0].Path;
        }

        if (matches.Count > 1)
        {
            var keys = string.Join(", ", matches.Select(m => m.Key));
            throw new InvalidOperationException(
                $"Site name '{siteName}' matches multiple local configs ({keys}). Use the config key instead.");
        }

        return directPath;
    }

    private static void NormalizeRaceSectionsEnabled(SiteConfig config)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>();

        foreach (var section in config.RaceSectionsEnabled ?? new List<string>())
        {
            var name = section?.Trim();
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (seen.Add(name))
                normalized.Add(name);
        }

        config.RaceSectionsEnabled = normalized;
    }

    /// <summary>
    /// Drops a site (or all sites) from the cache so the next GetSiteConfig
    /// re-reads from disk. Must be called after a site's JSON is edited/saved,
    /// otherwise stale config (e.g. affils) is served until app restart.
    /// </summary>
    public static void Invalidate(string siteName = null)
    {
        lock (cacheLock)
        {
            if (string.IsNullOrWhiteSpace(siteName))
            {
                ConfigCache.Clear();
            }
            else
            {
                ConfigCache.TryRemove(siteName, out _);

                foreach (var pair in ConfigCache.ToArray())
                {
                    var settings = pair.Value.SiteSettings;
                    if (settings == null)
                        continue;

                    if (string.Equals(settings.ConfigKey, siteName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(settings.Sitename, siteName, StringComparison.OrdinalIgnoreCase))
                    {
                        ConfigCache.TryRemove(pair.Key, out _);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Tries to get a site config, returning false if it doesn't exist.
    /// Non-throwing version of GetSiteConfig.
    /// </summary>
    public static bool TryGetSiteConfig(string siteName, out SiteConfig config)
    {
        if (ShouldIgnoreSite(siteName))
        {
            config = null;
            return false;
        }

        try
        {
            config = GetSiteConfig(siteName);
            return true;
        }
        catch (Exception ex)
        {
            var message = $"Failed to load site config '{siteName}': {ex.Message}";
            Console.WriteLine($"[SiteConfigManager] {message}");
            try { LogManager.Warning(message); } catch { }
            config = null;
            return false;
        }
    }
}
