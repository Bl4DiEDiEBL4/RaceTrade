using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;
using RaceTrade;

/// <summary>
/// Rules engine for evaluating releases against site-specific rules.
/// FIXED: Now properly handles FXP backend section lookups.
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

    private static string ReadMappedFxpBackendSection(JToken tag) =>
        (tag?[FxpBackendJsonKeys.SectionMap] ?? tag?[FxpBackendJsonKeys.LegacySectionMap])?.ToString();

    /// <summary>
    /// Loads rules for a specific FXP backend section from the site configuration.
    /// FIXED: Now correctly searches for FXP backend sections in tags instead of IRC names.
    /// </summary>
    /// <param name="jsonConfig">Site configuration JSON</param>
    /// <param name="fxpBackendSection">The FXP backend section to load rules for</param>
    public void LoadRules(JObject jsonConfig, string fxpBackendSection)
    {
        _sectionRules.Clear();
        _tagRules.Clear();

        if (jsonConfig == null)
        {
            LogManager.Error("[ERROR] jsonConfig is null in LoadRules");
            return;
        }

        if (string.IsNullOrEmpty(fxpBackendSection))
        {
            LogManager.Error("[ERROR] fxpBackendSection is null or empty in LoadRules");
            return;
        }

        // Find the section that contains a tag matching this FXP backend section
        var sections = jsonConfig["sections"] as JArray;
        if (sections == null || !sections.Any())
        {
            LogManager.Warning($"[WARN] No sections found in configuration");
            return;
        }

        JToken matchedSection = null;
        string matchedIrcSection = null;

        // Search for the IRC section that has a tag mapping to this FXP backend section
        foreach (var section in sections)
        {
            var sectionTags = section["tags"] as JArray;
            if (sectionTags == null) continue;

            foreach (var tag in sectionTags)
            {
                var mappedFxpBackendSection = ReadMappedFxpBackendSection(tag);
                if (string.Equals(mappedFxpBackendSection, fxpBackendSection, StringComparison.OrdinalIgnoreCase))
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
            LogManager.Debug($"[DEBUG] Found IRC section '{matchedIrcSection}' for FXP backend section '{fxpBackendSection}'");
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
                var mappedSection = ReadMappedFxpBackendSection(tag);
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
                        LogManager.Debug($"[DEBUG] Loaded {parsedTagRules.Count} rule(s) for FXP backend section '{mappedSection}'");
                    }
                }
            }
        }
    }

    /// <summary>
    /// Loads rules for a SPECIFIC IRC section (used when we know which IRC section to use).
    /// </summary>
    public void LoadRulesForIrcSection(JObject jsonConfig, string ircSection, string fxpBackendSection)
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
            LogManager.Debug($"[DEBUG] Loading rules from IRC section '{ircSection}' (FXP backend: '{fxpBackendSection}')");
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
                var mappedSection = ReadMappedFxpBackendSection(tag);
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
    /// Evaluates the input data against section and FXP backend tag rules.
    /// </summary>
    /// <param name="input">The input data to evaluate (key-value pairs)</param>
    /// <param name="fxpBackendSection">The FXP backend section name to evaluate rules for</param>
    /// <returns>"ALLOW" or "DROP" based on the evaluation</returns>
    public string Evaluate(Dictionary<string, string> input, string fxpBackendSection = null)
        => EvaluateDetailed(input, fxpBackendSection).Decision;

    /// <summary>
    /// Same evaluation as <see cref="Evaluate"/>, but returns which rule decided and in
    /// which phase, so the Test release tools can explain the outcome. Evaluate() is a
    /// thin wrapper over this, so the two can never disagree.
    /// </summary>
    public RuleEvaluation EvaluateDetailed(Dictionary<string, string> input, string fxpBackendSection = null)
    {
        if (input == null)
        {
            LogManager.Error("[ERROR] Input dictionary is null in Evaluate");
            return new RuleEvaluation(ACTION_DROP, null, "input was null");
        }

        // 0) EXCEPT rules (highest priority): force ALLOW, overriding any DROP.
        foreach (var rule in _sectionRules.Where(r =>
                     string.Equals(r.Action, ACTION_EXCEPT, StringComparison.OrdinalIgnoreCase)))
        {
            if (EvaluateRule(input, rule))
                return new RuleEvaluation(ACTION_ALLOW, rule, "section EXCEPT rule matched, forcing allow");
        }

        if (!string.IsNullOrEmpty(fxpBackendSection) &&
            _tagRules.TryGetValue(fxpBackendSection, out var exceptTagRules))
        {
            foreach (var rule in exceptTagRules.Where(r =>
                         string.Equals(r.Action, ACTION_EXCEPT, StringComparison.OrdinalIgnoreCase)))
            {
                if (EvaluateRule(input, rule))
                    return new RuleEvaluation(ACTION_ALLOW, rule, "mapping EXCEPT rule matched, forcing allow");
            }
        }

        // 1) GLOBAL (section) DROP rules
        foreach (var rule in _sectionRules.Where(r =>
                     string.Equals(r.Action, ACTION_DROP, StringComparison.OrdinalIgnoreCase)))
        {
            if (EvaluateRule(input, rule))
                return new RuleEvaluation(ACTION_DROP, rule, "section DROP rule matched");
        }

        // 2) TAG-SPECIFIC rules for this FXP backend section
        if (!string.IsNullOrEmpty(fxpBackendSection) &&
            _tagRules.TryGetValue(fxpBackendSection, out var tagSpecificRules))
        {
            foreach (var rule in tagSpecificRules.Where(r =>
                         string.Equals(r.Action, ACTION_DROP, StringComparison.OrdinalIgnoreCase)))
            {
                if (EvaluateRule(input, rule))
                    return new RuleEvaluation(ACTION_DROP, rule, "mapping DROP rule matched");
            }

            foreach (var rule in tagSpecificRules.Where(r =>
                         string.Equals(r.Action, ACTION_ALLOW, StringComparison.OrdinalIgnoreCase)))
            {
                if (EvaluateRule(input, rule))
                    return new RuleEvaluation(ACTION_ALLOW, rule, "mapping ALLOW rule matched");
            }
        }

        // 3) GLOBAL (section) ALLOW rules
        foreach (var rule in _sectionRules.Where(r =>
                     string.Equals(r.Action, ACTION_ALLOW, StringComparison.OrdinalIgnoreCase)))
        {
            if (EvaluateRule(input, rule))
                return new RuleEvaluation(ACTION_ALLOW, rule, "section ALLOW rule matched");
        }

        // 4) DEFAULT: ALLOW when nothing matched
        return new RuleEvaluation(ACTION_ALLOW, null, "no rule matched, default allow");
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

        // TRD.js-style negation: any operator except == / != can be prefixed with '!'
        // (e.g. "!isin", "!contains", "!iswm", "!matches").
        var op = rule.Operator.ToLowerInvariant();
        bool negate = false;
        if (op.Length > 1 && op[0] == '!' && op != "!=")
        {
            negate = true;
            op = op.Substring(1);
        }

        bool match = op switch
        {
            "==" => string.Equals(value, ruleValue, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(value, ruleValue, StringComparison.OrdinalIgnoreCase),
            "contains" => value.IndexOf(ruleValue, StringComparison.OrdinalIgnoreCase) >= 0,
            // Any entry of a comma/pipe list is contained in the value (TRD.js containsany).
            "containsany" => ContainsAny(value, ruleValue),
            "startswith" => value.StartsWith(ruleValue, StringComparison.OrdinalIgnoreCase),
            "endswith" => value.EndsWith(ruleValue, StringComparison.OrdinalIgnoreCase),
            "isin" => IsInList(value, ruleValue),

            // Numeric comparisons (TRD.js parity, e.g. "[year] >= 2020 ALLOW").
            // A non-numeric side simply doesn't match.
            ">=" => CompareNumeric(value, ruleValue) is int c1 && c1 >= 0,
            "<=" => CompareNumeric(value, ruleValue) is int c2 && c2 <= 0,
            ">" => CompareNumeric(value, ruleValue) is int c3 && c3 > 0,
            "<" => CompareNumeric(value, ruleValue) is int c4 && c4 < 0,

            // keep patterns intact
            "iswm" => IsWildcardMatch(value, ruleValue),
            "matches" => IsRegexMatch(value, ruleValue),

            _ => false
        };

        if (negate)
            match = !match;

        if (match)
            LogManager.Debug($"[✓] Rule matched: {rule.Key} {rule.Operator} {rule.Value}, Input='{value}'");

        return match;
    }

    /// <summary>
    /// Numerically compares value to ruleValue. Returns null (no match on any
    /// comparison operator) when either side is not a number.
    /// </summary>
    private static int? CompareNumeric(string value, string ruleValue)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var left))
            return null;
        if (!double.TryParse(ruleValue, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var right))
            return null;

        return left.CompareTo(right);
    }

    /// <summary>
    /// True when ANY entry of the comma/pipe-separated list appears in the value
    /// (substring, case-insensitive). TRD.js' containsany.
    /// </summary>
    private static bool ContainsAny(string value, string listString)
    {
        return listString
            .Split(new[] { ',', '|' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => item.Length > 0)
            .Any(item => value.IndexOf(item, StringComparison.OrdinalIgnoreCase) >= 0);
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

/// <summary>
/// Outcome of a rule evaluation: the decision, the rule that decided it (null for the
/// default), and a short human explanation. Used by the Test release tools.
/// </summary>
public sealed class RuleEvaluation
{
    public RuleEvaluation(string decision, Rule decidingRule, string reason)
    {
        Decision = decision;
        DecidingRule = decidingRule;
        Reason = reason;
    }

    public string Decision { get; }
    public Rule DecidingRule { get; }
    public string Reason { get; }

    public string DecidingRuleText => DecidingRule == null
        ? null
        : $"[{DecidingRule.Key}] {DecidingRule.Operator} {DecidingRule.Value} {DecidingRule.Action}".Trim();
}
