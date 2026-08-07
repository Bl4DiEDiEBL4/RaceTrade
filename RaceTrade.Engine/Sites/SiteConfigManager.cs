using System;
using System.Collections.Concurrent;
using System.IO;
using Newtonsoft.Json;
using RaceTrade;

/// <summary>
/// Centralized manager for loading and caching site configurations.
/// Used by FtpClientForm for on-demand site config loading.
/// </summary>
public static class SiteConfigManager
{
    private static readonly ConcurrentDictionary<string, SiteConfig> ConfigCache = new();
    private static readonly object cacheLock = new object();

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

            var filePath = Path.Combine("sites", $"{siteName}.json");

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException($"Site configuration file not found: {filePath}", filePath);
            }

            try
            {
                var json = File.ReadAllText(filePath);
                config = JsonConvert.DeserializeObject<SiteConfig>(json);

                if (config == null)
                {
                    throw new InvalidOperationException($"Failed to deserialize site configuration: {filePath}");
                }

                // Validate required fields
                if (config.SiteSettings == null)
                {
                    throw new InvalidOperationException($"Site configuration missing 'site_settings': {filePath}");
                }

                // Cache it
                ConfigCache[siteName] = config;
                Console.WriteLine($"[SiteConfigManager] Loaded and cached configuration for '{siteName}'");

                return config;
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"Invalid JSON in site configuration: {filePath}", ex);
            }
        }
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
                ConfigCache.Clear();
            else
                ConfigCache.TryRemove(siteName, out _);
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
        catch
        {
            config = null;
            return false;
        }
    }
}