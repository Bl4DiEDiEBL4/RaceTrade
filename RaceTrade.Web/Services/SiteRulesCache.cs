using System;
using System.IO;
using System.Linq;
using System.Text;

namespace RaceTrade.Web.Services;

/// <summary>
/// Disk cache for SITE RULES fetched from the FXP backend, so the rules stay
/// readable on the Site Rules tab of the site editor without refetching.
/// One text file per site config key under site_rules\ in the data folder;
/// the file write time doubles as the "last fetched" date.
/// </summary>
public static class SiteRulesCache
{
    private const string Dir = "site_rules";

    public static (string? Text, DateTime? FetchedUtc) Load(string? configKey)
    {
        var path = PathFor(configKey);
        if (path is null || !File.Exists(path))
            return (null, null);

        try
        {
            var text = File.ReadAllText(path, Encoding.UTF8);
            var fetched = File.GetLastWriteTimeUtc(path);
            return (text, fetched);
        }
        catch
        {
            return (null, null);
        }
    }

    public static DateTime? Save(string? configKey, string? text)
    {
        var path = PathFor(configKey);
        if (path is null)
            return null;

        try
        {
            Directory.CreateDirectory(Dir);
            AtomicFile.WriteAllText(path, text ?? "");
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return null;
        }
    }

    private static string? PathFor(string? configKey)
    {
        var key = (configKey ?? "").Trim();
        if (key.Length == 0)
            return null;

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(key.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return Path.Combine(Dir, safe + ".txt");
    }
}
