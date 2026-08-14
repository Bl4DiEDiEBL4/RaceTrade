using System.Text.RegularExpressions;

namespace RaceTrade.Web.Services;

public sealed record RegexTestLine(string Label, string Value, string Status = "");

public sealed record RegexTestResult(bool Success, string Title, IReadOnlyList<RegexTestLine> Lines);

public static class RegexTestHelper
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);
    private const RegexOptions DefaultOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static RegexTestResult TestPreBot(
        string? input,
        string? sectionRegex,
        string? releaseRegex,
        string? sectionPrefix,
        string? sectionSuffix)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Fail("Enter a prebot line to test.");

        if (string.IsNullOrWhiteSpace(sectionRegex) || string.IsNullOrWhiteSpace(releaseRegex))
            return Fail("Section regex and name regex are required for a useful test.");

        try
        {
            var section = Match(input, sectionRegex);
            var release = Match(input, releaseRegex);
            var trimmedSection = TrimPrefixSuffix(section.Group1, sectionPrefix, sectionSuffix);

            var lines = new List<RegexTestLine>
            {
                new("Input", input),
                new("Section match", section.Success ? "matched" : "no match", section.Success ? "ok" : "bad"),
                new("Section full match", section.FullMatch),
                new("Section group 1", section.Group1),
                new("Final section", trimmedSection),
                new("Release match", release.Success ? "matched" : "no match", release.Success ? "ok" : "bad"),
                new("Release full match", release.FullMatch),
                new("Release group 1", release.Group1)
            };

            var ok = section.Success && release.Success;
            return new RegexTestResult(ok, ok ? "PreBot line parsed" : "PreBot line did not parse", lines);
        }
        catch (ArgumentException ex)
        {
            return Fail($"Invalid regex: {ex.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            return Fail("Regex timed out. Make the pattern more specific.");
        }
    }

    public static RegexTestResult TestNormalAnnounce(
        string? input,
        string? newRegex,
        string? ignoreWordsText,
        string? sectionRegex,
        string? releaseRegex,
        string? sectionPrefix,
        string? sectionSuffix)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Fail("Enter an announce line to test.");

        if (string.IsNullOrWhiteSpace(sectionRegex) || string.IsNullOrWhiteSpace(releaseRegex))
            return Fail("Section regex and release regex are required for a useful test.");

        try
        {
            var newOk = string.IsNullOrWhiteSpace(newRegex) || IsMatch(input, newRegex);
            var section = Match(input, sectionRegex);
            var release = Match(input, releaseRegex);
            var ignoredWord = FirstIgnoredWord(input, ignoreWordsText);
            var trimmedSection = TrimPrefixSuffix(section.Group1, sectionPrefix, sectionSuffix);

            var lines = new List<RegexTestLine>
            {
                new("New/Race marker", newOk ? "matched" : "no match", newOk ? "ok" : "bad"),
                new("Section match", section.Success ? "matched" : "no match", section.Success ? "ok" : "bad"),
                new("Section full match", section.FullMatch),
                new("Section group 1", section.Group1),
                new("Final section", trimmedSection),
                new("Release match", release.Success ? "matched" : "no match", release.Success ? "ok" : "bad"),
                new("Release full match", release.FullMatch),
                new("Release group 1", release.Group1),
                new("Ignored word", string.IsNullOrEmpty(ignoredWord) ? "None" : ignoredWord, string.IsNullOrEmpty(ignoredWord) ? "ok" : "bad")
            };

            var ok = newOk && section.Success && release.Success && string.IsNullOrEmpty(ignoredWord);
            var title = ok
                ? "Normal announce parsed"
                : !newOk
                    ? "Line is not a new announce"
                    : !string.IsNullOrEmpty(ignoredWord)
                        ? "Line would be ignored"
                        : "Normal announce did not parse";

            return new RegexTestResult(ok, title, lines);
        }
        catch (ArgumentException ex)
        {
            return Fail($"Invalid regex: {ex.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            return Fail("Regex timed out. Make the pattern more specific.");
        }
    }

    public static RegexTestResult TestPreAnnounce(
        string? input,
        string? preRegex,
        string? sectionRegex,
        string? releaseRegex,
        string? sectionPrefix,
        string? sectionSuffix)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Fail("Enter a pre announce line to test.");

        if (string.IsNullOrWhiteSpace(preRegex))
            return Fail("Pre regex is empty. Add a marker such as PRE, or test the line with the normal announce patterns.");

        if (string.IsNullOrWhiteSpace(sectionRegex) || string.IsNullOrWhiteSpace(releaseRegex))
            return Fail("Pre section regex and pre release regex are required for a useful test.");

        try
        {
            var preOk = IsMatch(input, preRegex);
            var section = Match(input, sectionRegex);
            var release = Match(input, releaseRegex);
            var trimmedSection = TrimPrefixSuffix(section.Group1, sectionPrefix, sectionSuffix);

            var lines = new List<RegexTestLine>
            {
                new("Pre marker", preOk ? "matched" : "no match", preOk ? "ok" : "bad"),
                new("Section match", section.Success ? "matched" : "no match", section.Success ? "ok" : "bad"),
                new("Section full match", section.FullMatch),
                new("Section group 1", section.Group1),
                new("Final section", trimmedSection),
                new("Release match", release.Success ? "matched" : "no match", release.Success ? "ok" : "bad"),
                new("Release full match", release.FullMatch),
                new("Release group 1", release.Group1)
            };

            var ok = preOk && section.Success && release.Success;
            return new RegexTestResult(ok, ok ? "Pre announce parsed" : "Pre announce did not parse", lines);
        }
        catch (ArgumentException ex)
        {
            return Fail($"Invalid regex: {ex.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            return Fail("Regex timed out. Make the pattern more specific.");
        }
    }

    public static RegexTestResult TestIncompleteAnnounce(
        string? input,
        string? markerRegex,
        string? ignoreWordsText,
        string? sectionRegex,
        string? releaseRegex,
        string? sectionPrefix,
        string? sectionSuffix)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Fail("Enter an incomplete warning line to test.");

        if (string.IsNullOrWhiteSpace(markerRegex))
            return Fail("Incomplete marker regex is required.");

        if (string.IsNullOrWhiteSpace(sectionRegex) || string.IsNullOrWhiteSpace(releaseRegex))
            return Fail("Section regex and release regex are required for a useful test.");

        try
        {
            var markerOk = IsMatch(input, markerRegex);
            var section = Match(input, sectionRegex);
            var release = Match(input, releaseRegex);
            var ignoredWord = FirstIgnoredWord(input, ignoreWordsText);
            var trimmedSection = TrimPrefixSuffix(section.Group1, sectionPrefix, sectionSuffix);

            var lines = new List<RegexTestLine>
            {
                new("Incomplete marker", markerOk ? "matched" : "no match", markerOk ? "ok" : "bad"),
                new("Section match", section.Success ? "matched" : "no match", section.Success ? "ok" : "bad"),
                new("Section full match", section.FullMatch),
                new("Section group 1", section.Group1),
                new("Final section", trimmedSection),
                new("Release match", release.Success ? "matched" : "no match", release.Success ? "ok" : "bad"),
                new("Release full match", release.FullMatch),
                new("Release group 1", release.Group1),
                new("Ignored word", string.IsNullOrEmpty(ignoredWord) ? "None" : ignoredWord, string.IsNullOrEmpty(ignoredWord) ? "ok" : "bad")
            };

            var ok = markerOk && section.Success && release.Success && string.IsNullOrEmpty(ignoredWord);
            var title = ok
                ? "Incomplete warning parsed"
                : !markerOk
                    ? "Line is not an incomplete warning"
                    : !string.IsNullOrEmpty(ignoredWord)
                        ? "Line would be ignored"
                        : "Incomplete warning did not parse";

            return new RegexTestResult(ok, title, lines);
        }
        catch (ArgumentException ex)
        {
            return Fail($"Invalid regex: {ex.Message}");
        }
        catch (RegexMatchTimeoutException)
        {
            return Fail("Regex timed out. Make the pattern more specific.");
        }
    }

    private static RegexTestResult Fail(string message) =>
        new(false, message, new[] { new RegexTestLine("Status", message, "bad") });

    private static bool IsMatch(string input, string pattern) =>
        Regex.IsMatch(input, pattern, DefaultOptions, MatchTimeout);

    private static (bool Success, string FullMatch, string Group1) Match(string input, string pattern)
    {
        var match = Regex.Match(input, pattern, DefaultOptions, MatchTimeout);
        return (
            match.Success,
            match.Success ? match.Value : "",
            match.Success && match.Groups.Count > 1 ? match.Groups[1].Value : "");
    }

    private static string TrimPrefixSuffix(string value, string? prefix, string? suffix)
    {
        var result = value ?? "";

        if (!string.IsNullOrEmpty(prefix) && result.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            result = result[prefix.Length..];

        if (!string.IsNullOrEmpty(suffix) && result.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            result = result[..^suffix.Length];

        return result;
    }

    private static string FirstIgnoredWord(string input, string? ignoreWordsText) =>
        (ignoreWordsText ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(word => input.Contains(word, StringComparison.OrdinalIgnoreCase)) ?? "";
}
