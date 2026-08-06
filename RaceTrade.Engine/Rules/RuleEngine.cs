using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RaceTrade;

/// <summary>
/// Rules engine for evaluating releases against site-specific rules.
/// FIXED: Now properly handles CBFTP section lookups.
/// </summary>
public class RulesEngine
{
    public List<Rule> _sectionRules;
    public Dictionary<string, List<Rule>> _tagRules;

    // Constants for rule actions
    private const string ACTION_ALLOW = "ALLOW";
    private const string ACTION_DROP = "DROP";
    // need to add except
    private const string ACTION_EXCEPT = "EXCEPT";

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    public RulesEngine()
    {
        _sectionRules = new List<Rule>();
        _tagRules = new Dictionary<string, List<Rule>>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Loads rules for a specific CBFTP section from the site configuration.
    /// FIXED: Now correctly searches for CBFTP sections in tags instead of IRC names.
    /// </summary>
    /// <param name="jsonConfig">Site configuration JSON</param>
    /// <param name="cbftpSection">The CBFTP section to load rules for</param>
    public void LoadRules(JObject jsonConfig, string cbftpSection)
    {
        _sectionRules.Clear();
        _tagRules.Clear();

        if (jsonConfig == null)
        {
            LogManager.Error("[ERROR] jsonConfig is null in LoadRules");
            return;
        }

        if (string.IsNullOrEmpty(cbftpSection))
        {
            LogManager.Error("[ERROR] cbftpSection is null or empty in LoadRules");
            return;
        }

        // Find the section that contains a tag matching this CBFTP section
        var sections = jsonConfig["sections"] as JArray;
        if (sections == null || !sections.Any())
        {
            LogManager.Warning($"[WARN] No sections found in configuration");
            return;
        }

        JToken matchedSection = null;
        string matchedIrcSection = null;

        // Search for the IRC section that has a tag mapping to this CBFTP section
        foreach (var section in sections)
        {
            var sectionTags = section["tags"] as JArray;
            if (sectionTags == null) continue;

            foreach (var tag in sectionTags)
            {
                var mappedCbftpSection = tag["map_cbftp_section"]?.ToString();
                if (string.Equals(mappedCbftpSection, cbftpSection, StringComparison.OrdinalIgnoreCase))
                {
                    matchedIrcSection = section["irc_name"]?.ToString();
                    matchedSection = section;
                    break;
                }
            }

            if (matchedSection != null) break;
        }

        if (matchedSection == null)
        {
            return;
        }

        if (EngineSettings.DebugEnabled)
        {
            LogManager.Debug($"[DEBUG] Found IRC section '{matchedIrcSection}' for CBFTP section '{cbftpSection}'");
        }

        // Load global section rules (apply to all tags in this section)
        var globalRules = matchedSection["rules"]?.ToObject<List<string>>() ?? new List<string>();
        foreach (var ruleString in globalRules)
        {
            var parsedRule = ParseRule(ruleString);
            if (parsedRule != null)
            {
                _sectionRules.Add(parsedRule);
            }
        }

        if (EngineSettings.DebugEnabled && _sectionRules.Any())
        {
            LogManager.Debug($"[DEBUG] Loaded {_sectionRules.Count} global section rule(s) for '{matchedIrcSection}'");
        }

        // Load tag-specific rules
        var matchedTags = matchedSection["tags"] as JArray;
        if (matchedTags != null)
        {
            foreach (var tag in matchedTags)
            {
                var mappedSection = tag["map_cbftp_section"]?.ToString();
                if (string.IsNullOrEmpty(mappedSection)) continue;

                var tagRules = tag["rules"]?.ToObject<List<string>>() ?? new List<string>();
                var parsedTagRules = tagRules
                    .Select(ParseRule)
                    .Where(rule => rule != null)
                    .ToList();

                if (parsedTagRules.Any())
                {
                    _tagRules[mappedSection] = parsedTagRules;

                    if (EngineSettings.DebugEnabled)
                    {
                        LogManager.Debug($"[DEBUG] Loaded {parsedTagRules.Count} rule(s) for CBFTP section '{mappedSection}'");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Loads rules for a SPECIFIC IRC section (used when we know which IRC section to use).
    /// </summary>
    public void LoadRulesForIrcSection(JObject jsonConfig, string ircSection, string cbftpSection)
    {
        _sectionRules.Clear();
        _tagRules.Clear();

        if (jsonConfig == null || string.IsNullOrEmpty(ircSection))
            return;

        var sections = jsonConfig["sections"] as JArray;
        if (sections == null) return;

        // Find the specific IRC section
        var matchedSection = sections.FirstOrDefault(s =>
            string.Equals((string)s["irc_name"], ircSection, StringComparison.OrdinalIgnoreCase));

        if (matchedSection == null)
            return;

        if (EngineSettings.DebugEnabled)
        {
            LogManager.Debug($"[DEBUG] Loading rules from IRC section '{ircSection}' (CBFTP: '{cbftpSection}')");
        }

        // Load section rules
        var globalRules = matchedSection["rules"]?.ToObject<List<string>>() ?? new List<string>();
        foreach (var ruleString in globalRules)
        {
            var parsedRule = ParseRule(ruleString);
            if (parsedRule != null)
            {
                _sectionRules.Add(parsedRule);
            }
        }

        // Load tag rules
        var tags = matchedSection["tags"] as JArray;
        if (tags != null)
        {
            foreach (var tag in tags)
            {
                var mappedSection = tag["map_cbftp_section"]?.ToString();
                if (string.IsNullOrEmpty(mappedSection)) continue;

                var tagRules = tag["rules"]?.ToObject<List<string>>() ?? new List<string>();
                var parsedTagRules = tagRules
                    .Select(ParseRule)
                    .Where(rule => rule != null)
                    .ToList();

                if (parsedTagRules.Any())
                {
                    _tagRules[mappedSection] = parsedTagRules;
                }
            }
        }
    }





    /// <summary>
    /// Evaluates the input data against section and CBFTP tag rules.
    /// </summary>
    /// <param name="input">The input data to evaluate (key-value pairs)</param>
    /// <param name="cbftpSection">The CBFTP section name to evaluate rules for</param>
    /// <returns>"ALLOW" or "DROP" based on the evaluation</returns>
    public string Evaluate(Dictionary<string, string> input, string cbftpSection = null)
    {
        if (input == null)
        {
            LogManager.Error("[ERROR] Input dictionary is null in Evaluate");
            return ACTION_DROP;
        }

        if (EngineSettings.DebugEnabled)
        {
            LogManager.Debug($"[DEBUG] Evaluating rules for CBFTP section '{cbftpSection}'");
        }

        // --------------------------------------------------------------------------------
        // 0) EXCEPT rules (highest priority): an explicit carve-out that forces ALLOW,
        //    overriding any DROP that would otherwise match. ("drop these, EXCEPT when...")
        //    Evaluated global-first, then tag-specific for this CBFTP section.
        // --------------------------------------------------------------------------------
        foreach (var rule in _sectionRules.Where(r =>
                     string.Equals(r.Action, ACTION_EXCEPT, StringComparison.OrdinalIgnoreCase)))
        {
            if (EvaluateRule(input, rule))
            {
                if (EngineSettings.DebugEnabled)
                    LogManager.Success($"[EXCEPT] Global EXCEPT rule matched: {rule.Key} {rule.Operator} {rule.Value}. Forcing ALLOW.");
                return ACTION_ALLOW;
            }
        }

        if (!string.IsNullOrEmpty(cbftpSection) &&
            _tagRules.TryGetValue(cbftpSection, out var exceptTagRules))
        {
            foreach (var rule in exceptTagRules.Where(r =>
                         string.Equals(r.Action, ACTION_EXCEPT, StringComparison.OrdinalIgnoreCase)))
            {
                if (EvaluateRule(input, rule))
                {
                    if (EngineSettings.DebugEnabled)
                        LogManager.Success($"[EXCEPT] CBFTP EXCEPT rule matched: {rule.Key} {rule.Operator} {rule.Value}. Forcing ALLOW.");
                    return ACTION_ALLOW;
                }
            }
        }

        // --------------------------------------------------------------------------------
        // 1) GLOBAL DROP rules
        // --------------------------------------------------------------------------------
        foreach (var rule in _sectionRules.Where(r =>
                     string.Equals(r.Action, ACTION_DROP, StringComparison.OrdinalIgnoreCase)))
        {
            if (EvaluateRule(input, rule))
            {
                if (EngineSettings.DebugEnabled)
                {
                    var inputValue = input.ContainsKey(rule.Key) ? input[rule.Key] : "N/A";
                    LogManager.Error($"[DROP] Global DROP rule matched: {rule.Key} {rule.Operator} {rule.Value}. Input: {inputValue}");
                }
                return ACTION_DROP;
            }
        }

        // --------------------------------------------------------------------------------
        // 2) TAG-SPECIFIC RULES for this CBFTP section
        //    2a) Tag DROP rules first
        //    2b) Then Tag ALLOW rules
        // --------------------------------------------------------------------------------
        if (!string.IsNullOrEmpty(cbftpSection) &&
            _tagRules.TryGetValue(cbftpSection, out var tagSpecificRules))
        {
            // 2a) Tag DROP rules
            foreach (var rule in tagSpecificRules.Where(r =>
                         string.Equals(r.Action, ACTION_DROP, StringComparison.OrdinalIgnoreCase)))
            {
                if (EvaluateRule(input, rule))
                {
                    LogManager.Debug($"[DROP] CBFTP rule matched: {rule.Key} {rule.Operator} {rule.Value}");
                    return ACTION_DROP;
                }
            }

            // 2b) Tag ALLOW rules
            foreach (var rule in tagSpecificRules.Where(r =>
                         string.Equals(r.Action, ACTION_ALLOW, StringComparison.OrdinalIgnoreCase)))
            {
                if (EvaluateRule(input, rule))
                {
                    LogManager.Debug($"[ALLOW] CBFTP rule matched: {rule.Key} {rule.Operator} {rule.Value}");
                    return ACTION_ALLOW;
                }
            }

            // EXCEPT rules are handled up-front (step 0) as allow-overrides.
        }

        // --------------------------------------------------------------------------------
        // 3) GLOBAL ALLOW rules (fall-back allow layer)
        // --------------------------------------------------------------------------------
        foreach (var rule in _sectionRules.Where(r =>
                     string.Equals(r.Action, ACTION_ALLOW, StringComparison.OrdinalIgnoreCase)))
        {
            if (EvaluateRule(input, rule))
            {
                if (EngineSettings.DebugEnabled)
                {
                    LogManager.Success($"[ALLOW] Global ALLOW rule matched: {rule.Key} {rule.Operator} {rule.Value}");
                }
                return ACTION_ALLOW;
            }
        }

        // --------------------------------------------------------------------------------
        // 4) DEFAULT: ALLOW if nothing matched
        // --------------------------------------------------------------------------------
        if (EngineSettings.DebugEnabled)
        {
            LogManager.Success("[ALLOW] No rules matched. Default: ALLOW");
        }

        return ACTION_ALLOW;
    }



    /// <summary>
    /// Evaluates a single rule against the input data.
    /// </summary>
    private bool EvaluateRule(Dictionary<string, string> input, Rule rule)
    {
        if (!input.TryGetValue(rule.Key, out var value))
        {
            if (EngineSettings.DebugEnabled)
                LogManager.Warning($"[DEBUG] Key '{rule.Key}' not found in input. Skipping rule.");
            return false;
        }

        value ??= string.Empty;
        var ruleValue = rule.Value ?? string.Empty;

        bool match = rule.Operator.ToLowerInvariant() switch
        {
            "==" => string.Equals(value, ruleValue, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(value, ruleValue, StringComparison.OrdinalIgnoreCase),
            "contains" => value.IndexOf(ruleValue, StringComparison.OrdinalIgnoreCase) >= 0,
            "startswith" => value.StartsWith(ruleValue, StringComparison.OrdinalIgnoreCase),
            "endswith" => value.EndsWith(ruleValue, StringComparison.OrdinalIgnoreCase),
            "isin" => IsInList(value, ruleValue),

            // keep patterns intact
            "iswm" => IsWildcardMatch(value, ruleValue),
            "matches" => IsRegexMatch(value, ruleValue),

            _ => false
        };

        if (match)
            LogManager.Debug($"[✓] Rule matched: {rule.Key} {rule.Operator} {rule.Value}, Input='{value}'");

        return match;
    }

    /// <summary>
    /// Parses a rule string into a Rule object.
    /// </summary>
    private Rule ParseRule(string ruleString)
    {
        if (string.IsNullOrWhiteSpace(ruleString))
            return null;

        var parts = ruleString.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            LogManager.Warning($"[WARN] Invalid rule format: '{ruleString}'");
            return null;
        }

        string key = parts[0].Trim('[', ']');
        string op = parts[1];

        string last = parts[parts.Length - 1];

        // Only treat the trailing token as an ACTION when a value token still remains
        // (parts.Length > 3). Otherwise a rule whose VALUE is literally "DROP"/"ALLOW"
        // (e.g. "[release] contains DROP") would be parsed as having no value and
        // silently discarded.
        bool lastLooksLikeAction =
            string.Equals(last, ACTION_ALLOW, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(last, ACTION_DROP, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(last, ACTION_EXCEPT, StringComparison.OrdinalIgnoreCase);

        bool hasAction = lastLooksLikeAction && parts.Length > 3;

        string action = hasAction ? last : ACTION_ALLOW;

        int valueStart = 2;
        int valueEnd = hasAction ? parts.Length - 2 : parts.Length - 1;

        if (valueEnd < valueStart)
            return null;

        string value = string.Join(" ", parts.Skip(valueStart).Take(valueEnd - valueStart + 1));

        return new Rule
        {
            Key = key,
            Operator = op,
            Value = value,
            Action = action
        };
    }

 

    /// <summary>
    /// Performs wildcard matching (* and ?).
    /// </summary>
    private bool IsWildcardMatch(string value, string pattern)
    {
        try
        {
            var regexPattern = "^" + Regex.Escape(pattern)
                .Replace("\\*", ".*")
                .Replace("\\?", ".") + "$";

            return Regex.IsMatch(value, regexPattern, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (Exception ex)
        {
            LogManager.Error($"[ERROR] Wildcard regex error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Performs regex matching.
    /// </summary>
    private bool IsRegexMatch(string value, string pattern)
    {
        try
        {
            return Regex.IsMatch(value ?? string.Empty, pattern, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (Exception ex)
        {
            LogManager.Error($"[ERROR] Regex match error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks if value is EQUAL to one of the entries in a comma or pipe-separated list.
    /// This is membership, not containment: "[group] isin GRP1,GRP2" must not match a
    /// group of "MYGRP12". Use 'contains' if you want substring behaviour.
    /// </summary>
    private bool IsInList(string value, string listString)
    {
        return listString
            .Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Any(item => string.Equals(value, item.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Represents a single rule for filtering releases.
/// </summary>
public class Rule
{
    public string Key { get; set; }
    public string Operator { get; set; }
    public string Value { get; set; }
    public string Action { get; set; } // ALLOW, DROP, EXCEPT
}