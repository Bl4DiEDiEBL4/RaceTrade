using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;

public static class SectionDetectionModes
{
    public const string UseParsed = "parsed";
    public const string ParsedThenClassifier = "parsed_plus_classifier";
    public const string ClassifierOnly = "classifier";

    public static string Normalize(string mode) => mode switch
    {
        ParsedThenClassifier => ParsedThenClassifier,
        ClassifierOnly => ClassifierOnly,
        _ => UseParsed
    };
}

public sealed class ReleaseClassifierConfig
{
    [JsonProperty("rules")]
    public List<ReleaseClassifierRule> Rules { get; set; } = new();

    [JsonProperty("language_mappings", ObjectCreationHandling = ObjectCreationHandling.Replace)]
    public List<ReleaseLanguageMapping> LanguageMappings { get; set; } = new();

    /// <summary>
    /// Opt-in strict language detection: skip the title (scan only after the episode
    /// token for TV / after the year for movies) and never scan the group name, so
    /// titles like Made.In.China or groups like RUS can't register as a language.
    /// Defaults to false so existing configs keep their exact current behavior.
    /// </summary>
    [JsonProperty("strict_language_detection")]
    public bool StrictLanguageDetection { get; set; }
}

public sealed class ReleaseLanguageMapping
{
    [JsonProperty("code")]
    public string Code { get; set; } = "";

    [JsonProperty("aliases")]
    public List<string> Aliases { get; set; } = new();

    public static List<ReleaseLanguageMapping> Defaults() => new()
    {
        new() { Code = "DE", Aliases = new() { "GERMAN", "SHiTONLYGERMAN" } },
        new() { Code = "FR", Aliases = new() { "FRENCH", "TRUEFRENCH", "TREUFRENCH", "SUBFRENCH", "FRE" } },
        new() { Code = "NL", Aliases = new() { "DUTCH", "NLSUBBED", "Nederlandse", "NEDERLAND" } },
        new() { Code = "PL", Aliases = new() { "POLiSH", "POLISH", "PLDUB" } },
        new() { Code = "NO", Aliases = new() { "NORWEGiAN", "NORWEGIAN", "NORWAY", "NOSUB" } },
        new() { Code = "SE", Aliases = new() { "SWEDiSH", "SWEDISH", "SWESUB" } },
        new() { Code = "DA", Aliases = new() { "DANISH", "DANiSH" } },
        new() { Code = "ES", Aliases = new() { "SPANiSH", "SPANISH", "CATALAN", "SPASUBS", "SP" } },
        new() { Code = "FI", Aliases = new() { "FiNSUB", "FiNNiSH", "FINNISH" } },
        new() { Code = "IT", Aliases = new() { "ITALIAN" } },
        new() { Code = "JP", Aliases = new() { "JPN", "JAP", "Japanese", "JAPANESE" } },
        new() { Code = "KR", Aliases = new() { "KOR", "Korean", "KOREAN" } },
        new() { Code = "HU", Aliases = new() { "HUN", "Hungarian", "HUNGARIAN" } },
        new() { Code = "PT", Aliases = new() { "PORTUGUESE" } },
        new() { Code = "SK", Aliases = new() { "SLOVAK" } },
        new() { Code = "CZ", Aliases = new() { "CZECH" } },
        new() { Code = "RU", Aliases = new() { "RUSSIAN", "RUS" } },
        new() { Code = "LT", Aliases = new() { "LATINO", "LiTHUANiAN", "LITHUANIAN" } },
        new() { Code = "SL", Aliases = new() { "SLOSUBS" } },
        new() { Code = "FL", Aliases = new() { "FLEMISH" } },
        new() { Code = "NORDIC", Aliases = new() { "NORDIC" } },
        new() { Code = "BH", Aliases = new() { "BHANGRA" } },
        new() { Code = "BR", Aliases = new() { "BRAZiLiAN", "BRAZILIAN" } },
        new() { Code = "AI", Aliases = new() { "ASiA", "ASIA" } },
        new() { Code = "AU", Aliases = new() { "AUSTRALIA", "AUSTRALIAN" } },
        new() { Code = "UA", Aliases = new() { "UKRAINIAN" } },
        new() { Code = "CI", Aliases = new() { "CHINA" } },
        new() { Code = "HB", Aliases = new() { "HEBREW" } },
        new() { Code = "LA", Aliases = new() { "LATIN" } },
        new() { Code = "GR", Aliases = new() { "GREEK" } },
        new() { Code = "IS", Aliases = new() { "ICELANDIC" } },
        new() { Code = "EE", Aliases = new() { "ESTONiAN", "ESTONIAN" } },
        new() { Code = "BL", Aliases = new() { "BALTiC", "BALTIC" } },
        new() { Code = "SR", Aliases = new() { "SERBIAN" } },
        new() { Code = "BO", Aliases = new() { "BOSNIAN" } }
    };
}

public sealed class ReleaseClassifierRule
{
    [JsonProperty("name")]
    public string Name { get; set; } = "";

    [JsonProperty("from_section")]
    public string FromSection { get; set; } = "GENERAL";

    [JsonProperty("match")]
    public string Match { get; set; } = "";

    [JsonProperty("set_section")]
    public string SetSection { get; set; } = "";

    [JsonProperty("continue")]
    public bool Continue { get; set; }
}

public sealed class ReleaseClassifierResult
{
    public string Mode { get; init; } = SectionDetectionModes.UseParsed;
    public string StartSection { get; init; } = "";
    public string FinalSection { get; init; } = "";
    public string StopReason { get; init; } = "";
    public List<ReleaseClassifierStep> Steps { get; init; } = new();
}

public sealed class ReleaseClassifierStep
{
    public int Pass { get; init; }
    public string FromSection { get; init; } = "";
    public string ToSection { get; init; } = "";
    public string RuleName { get; init; } = "";
    public string Match { get; init; } = "";
}

public static class ReleaseClassifier
{
    private static readonly object Gate = new();
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly string ConfigPath = Path.Combine("settings", "release_classifier.json");
    private static ReleaseClassifierConfig _cached;
    private static DateTime _cachedWriteUtc;

    // First point where the title is certainly over: episode token (TV) or year
    // (movies). Strict language detection only scans what comes after it.
    private static readonly Regex StrictLanguageAnchor = new(
        @"[\s._-](S\d+E\d+(?:-?E\d+)?|S\d+|\d+x\d+|(?:Episode|Part)\.?\d+|(?:19|20)\d{2}(?:\.\d{2}\.\d{2})?)(?=[\s._-]|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexTimeout);

    public static ReleaseClassifierConfig Load()
    {
        lock (Gate)
        {
            Directory.CreateDirectory("settings");
            if (!File.Exists(ConfigPath))
                return new ReleaseClassifierConfig();

            var writeUtc = File.GetLastWriteTimeUtc(ConfigPath);
            if (_cached is not null && writeUtc == _cachedWriteUtc)
                return Clone(_cached);

            var loaded = JsonConvert.DeserializeObject<ReleaseClassifierConfig>(File.ReadAllText(ConfigPath)) ?? new ReleaseClassifierConfig();
            loaded.Rules ??= new List<ReleaseClassifierRule>();
            loaded.LanguageMappings = NormalizeLanguageMappings(loaded.LanguageMappings, useDefaultsWhenEmpty: true);
            _cached = loaded;
            _cachedWriteUtc = writeUtc;
            return Clone(loaded);
        }
    }

    public static void Save(ReleaseClassifierConfig config)
    {
        lock (Gate)
        {
            Directory.CreateDirectory("settings");
            config.Rules ??= new List<ReleaseClassifierRule>();
            config.LanguageMappings = NormalizeLanguageMappings(config.LanguageMappings, useDefaultsWhenEmpty: true);
            File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(config, Formatting.Indented));
            _cached = Clone(config);
            _cachedWriteUtc = File.GetLastWriteTimeUtc(ConfigPath);
        }
    }

    public static ReleaseClassifierResult Classify(string releaseName, string parsedSection, string mode)
    {
        // "parsed" mode never touches rules or language mappings, so skip the
        // config load (disk stat + full clone) on that per-announce path.
        var normalizedMode = SectionDetectionModes.Normalize(mode);
        return normalizedMode == SectionDetectionModes.UseParsed
            ? Classify(releaseName, parsedSection, normalizedMode, null)
            : Classify(releaseName, parsedSection, normalizedMode, Load());
    }

    public static ReleaseClassifierResult Classify(string releaseName, string parsedSection, string mode, ReleaseClassifierConfig config)
    {
        var normalizedMode = SectionDetectionModes.Normalize(mode);
        var parsed = CleanSection(parsedSection);
        var start = normalizedMode == SectionDetectionModes.ClassifierOnly
            ? "GENERAL"
            : (string.IsNullOrWhiteSpace(parsed) ? "GENERAL" : parsed);

        if (normalizedMode == SectionDetectionModes.UseParsed)
        {
            return new ReleaseClassifierResult
            {
                Mode = normalizedMode,
                StartSection = start,
                FinalSection = string.IsNullOrWhiteSpace(parsed) ? start : parsed
            };
        }

        config ??= new ReleaseClassifierConfig();
        config.Rules ??= new List<ReleaseClassifierRule>();
        config.LanguageMappings = NormalizeLanguageMappings(config.LanguageMappings, useDefaultsWhenEmpty: true);

        var rules = config.Rules
            .Where(r => !string.IsNullOrWhiteSpace(r.FromSection) && !string.IsNullOrWhiteSpace(r.SetSection))
            .ToList();

        var section = start;
        var stopReason = "";
        var steps = new List<ReleaseClassifierStep>();
        var attributes = BuildAttributes(releaseName, section, config.LanguageMappings, config.StrictLanguageDetection);

        for (var pass = 1; pass <= 5; pass++)
        {
            attributes["section"] = section;
            var rule = rules.FirstOrDefault(r => SectionMatches(r.FromSection, section) && RuleMatches(r.Match, attributes));
            if (rule is null)
            {
                stopReason = rules.Any(r => SectionMatches(r.FromSection, section))
                    ? $"No rule in {section} matched this release."
                    : $"No rules configured for {section}.";
                break;
            }

            var next = ApplyTemplate(rule.SetSection, attributes);
            if (string.IsNullOrWhiteSpace(next))
            {
                stopReason = $"Rule {DisplayRuleName(rule)} produced an empty section.";
                break;
            }

            steps.Add(new ReleaseClassifierStep
            {
                Pass = pass,
                FromSection = section,
                ToSection = next,
                RuleName = string.IsNullOrWhiteSpace(rule.Name) ? rule.Match : rule.Name,
                Match = rule.Match ?? ""
            });

            section = next;
            if (!rule.Continue)
            {
                stopReason = $"Rule {DisplayRuleName(rule)} stopped here because continue is off.";
                break;
            }
        }

        if (stopReason.Length == 0 && steps.Count > 0)
            stopReason = $"Stopped after the maximum of 5 passes at {section}.";

        return new ReleaseClassifierResult
        {
            Mode = normalizedMode,
            StartSection = start,
            FinalSection = section,
            StopReason = stopReason,
            Steps = steps
        };
    }

    private static string DisplayRuleName(ReleaseClassifierRule rule) =>
        string.IsNullOrWhiteSpace(rule.Name) ? $"'{rule.Match}'" : $"'{rule.Name}'";

    public static bool TryDetectLanguage(string releaseName, out string language, out string code)
    {
        var config = CachedConfig();
        language = DetectLanguage(releaseName ?? "", config.LanguageMappings, config.StrictLanguageDetection, out code);
        return !string.IsNullOrWhiteSpace(language) && !string.IsNullOrWhiteSpace(code);
    }

    /// <summary>
    /// Read-only view of the cached config's language mappings for the per-announce
    /// hot path (RaceHelper.ParseReleaseName). DetectLanguage only reads, and Load/
    /// Save replace the cached instance wholesale, so handing out the reference is
    /// safe and skips the full Clone that Load() performs on every call.
    /// </summary>
    private static ReleaseClassifierConfig CachedConfig()
    {
        lock (Gate)
        {
            if (_cached is not null && File.Exists(ConfigPath) &&
                File.GetLastWriteTimeUtc(ConfigPath) == _cachedWriteUtc)
            {
                return _cached;
            }
        }

        return Load();
    }

    private static Dictionary<string, string> BuildAttributes(string releaseName, string section, IEnumerable<ReleaseLanguageMapping> languageMappings, bool strictLanguage)
    {
        var attributes = RaceHelper.ParseReleaseName(releaseName ?? "") ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        attributes = new Dictionary<string, string>(attributes, StringComparer.OrdinalIgnoreCase)
        {
            ["release"] = releaseName ?? "",
            ["section"] = section ?? ""
        };

        var detectedLanguage = DetectLanguage(releaseName ?? "", languageMappings, strictLanguage, out var detectedCode);
        if (!string.IsNullOrWhiteSpace(detectedLanguage))
        {
            attributes["language"] = detectedLanguage;
            attributes["lang"] = detectedCode;
        }
        else if (attributes.TryGetValue("language", out var language) && !string.IsNullOrWhiteSpace(language))
        {
            attributes["lang"] = ToLanguageSuffix(language, languageMappings);
        }

        return attributes;
    }

    private static bool SectionMatches(string pattern, string section)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Trim() == "*")
            return true;

        return pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(p => string.Equals(p, section, StringComparison.OrdinalIgnoreCase));
    }

    private static bool RuleMatches(string expression, Dictionary<string, string> attributes)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return true;

        var orParts = Regex.Split(expression, @"\s+\|\|\s+", RegexOptions.IgnoreCase, RegexTimeout);
        return orParts.Any(orPart => Regex.Split(orPart, @"\s+&&\s+", RegexOptions.IgnoreCase, RegexTimeout)
            .All(part => ConditionMatches(part.Trim(), attributes)));
    }

    private static bool ConditionMatches(string condition, Dictionary<string, string> attributes)
    {
        var match = Regex.Match(condition, @"^\[(?<key>[^\]]+)\]\s+(?<op>notin|notcontains|contains|equals|is|regex|in|exists|notexists)\s*(?<value>.*)$", RegexOptions.IgnoreCase, RegexTimeout);
        if (!match.Success)
            return false;

        var key = match.Groups["key"].Value.Trim();
        var op = match.Groups["op"].Value.Trim().ToLowerInvariant();
        var expected = TrimQuotes(match.Groups["value"].Value.Trim());
        attributes.TryGetValue(key, out var actual);
        actual ??= "";

        return op switch
        {
            "exists" => !string.IsNullOrWhiteSpace(actual),
            "notexists" => string.IsNullOrWhiteSpace(actual),
            "contains" => actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "notcontains" => !actual.Contains(expected, StringComparison.OrdinalIgnoreCase),
            "equals" or "is" => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase),
            "in" => SplitList(expected).Any(x => string.Equals(actual, x, StringComparison.OrdinalIgnoreCase)),
            "notin" => !SplitList(expected).Any(x => string.Equals(actual, x, StringComparison.OrdinalIgnoreCase)),
            "regex" => Regex.IsMatch(actual, expected, RegexOptions.IgnoreCase, RegexTimeout),
            _ => false
        };
    }

    private static string ApplyTemplate(string template, Dictionary<string, string> attributes)
    {
        return Regex.Replace(template ?? "", @"\{([^}]+)\}", m =>
        {
            var key = m.Groups[1].Value.Trim();
            return attributes.TryGetValue(key, out var value) ? value : "";
        }, RegexOptions.IgnoreCase, RegexTimeout).Trim();
    }

    private static IEnumerable<string> SplitList(string value) =>
        (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string TrimQuotes(string value) =>
        value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    private static string CleanSection(string section) => string.IsNullOrWhiteSpace(section) ? "" : section.Trim();

    private static bool IsPlainToken(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.All(char.IsLetterOrDigit);

    private static string ToLanguageSuffix(string language, IEnumerable<ReleaseLanguageMapping> languageMappings)
    {
        if (string.IsNullOrWhiteSpace(language))
            return "";

        foreach (var mapping in languageMappings ?? ReleaseLanguageMapping.Defaults())
        {
            if (string.Equals(mapping.Code, language, StringComparison.OrdinalIgnoreCase) ||
                (mapping.Aliases ?? new List<string>()).Any(a => string.Equals(a, language, StringComparison.OrdinalIgnoreCase)))
            {
                return mapping.Code.ToUpperInvariant();
            }
        }

        return language.ToUpperInvariant();
    }

    private static string DetectLanguage(string releaseName, IEnumerable<ReleaseLanguageMapping> languageMappings, bool strict, out string code)
    {
        code = "";
        if (string.IsNullOrWhiteSpace(releaseName))
            return "";

        // Strict mode scans only the part after the title (episode token / year).
        // Plain-token aliases additionally never see the group name; raw aliases
        // like "-NO-" keep the group separator region so "WEB-NO-GRP" still hits.
        var containsSource = strict ? StrictLanguageScanRegion(releaseName) : releaseName;
        var tokenSource = strict ? StripReleaseGroup(containsSource) : releaseName;

        var tokens = Regex.Split(tokenSource, @"[^A-Za-z0-9]+", RegexOptions.None, RegexTimeout)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mapping in languageMappings ?? ReleaseLanguageMapping.Defaults())
        {
            foreach (var alias in (mapping.Aliases ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)))
            {
                var trimmedAlias = alias.Trim();
                var aliasMatches = IsPlainToken(trimmedAlias)
                    ? tokens.Contains(trimmedAlias)
                    : containsSource.Contains(trimmedAlias, StringComparison.OrdinalIgnoreCase);

                if (aliasMatches)
                {
                    code = (mapping.Code ?? trimmedAlias).Trim().ToUpperInvariant();
                    return trimmedAlias;
                }
            }
        }

        return "";
    }

    /// <summary>
    /// Everything after the first episode token (TV) or year (movies). If neither
    /// exists, the full name is returned so detection still has something to scan.
    /// </summary>
    private static string StrictLanguageScanRegion(string releaseName)
    {
        try
        {
            var anchor = StrictLanguageAnchor.Match(releaseName);
            if (anchor.Success)
                return releaseName.Substring(anchor.Index + anchor.Length);
        }
        catch (RegexMatchTimeoutException)
        {
            // fall through to the full name
        }

        return releaseName;
    }

    /// <summary>
    /// Cuts the release group (everything after the last '-') so group names like
    /// RUS or SP can never register as a language token.
    /// </summary>
    private static string StripReleaseGroup(string value)
    {
        var lastDash = value.LastIndexOf('-');
        return lastDash > 0 ? value.Substring(0, lastDash) : value;
    }

    private static List<ReleaseLanguageMapping> NormalizeLanguageMappings(
        IEnumerable<ReleaseLanguageMapping> mappings,
        bool useDefaultsWhenEmpty)
    {
        var source = (mappings ?? Enumerable.Empty<ReleaseLanguageMapping>())
            .Where(m => !string.IsNullOrWhiteSpace(m.Code))
            .ToList();

        if (source.Count == 0 && useDefaultsWhenEmpty)
            source = ReleaseLanguageMapping.Defaults();

        return source
            .GroupBy(m => m.Code.Trim().ToUpperInvariant(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new ReleaseLanguageMapping
            {
                Code = g.Key,
                Aliases = g
                    .SelectMany(m => m.Aliases ?? new List<string>())
                    .Select(a => (a ?? "").Trim())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList()
            })
            .OrderBy(m => m.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ReleaseClassifierConfig Clone(ReleaseClassifierConfig source) => new()
    {
        Rules = (source.Rules ?? new List<ReleaseClassifierRule>()).Select(r => new ReleaseClassifierRule
        {
            Name = r.Name ?? "",
            FromSection = r.FromSection ?? "GENERAL",
            Match = r.Match ?? "",
            SetSection = r.SetSection ?? "",
            Continue = r.Continue
        }).ToList(),
        LanguageMappings = NormalizeLanguageMappings(source.LanguageMappings, useDefaultsWhenEmpty: true),
        StrictLanguageDetection = source.StrictLanguageDetection
    };
}