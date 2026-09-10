using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RaceTrade.Web.Services;

public sealed class ReleaseSearchService
{
    private static readonly Regex ReleaseLikeRegex = new(
        @"(?<release>[A-Za-z0-9][A-Za-z0-9._+\-()]+-[A-Za-z0-9][A-Za-z0-9._+\-()]*)",
        RegexOptions.Compiled);

    private static readonly Regex PathRegex = new(
        @"(?<![:/])(?<path>/[^\s\|]+)",
        RegexOptions.Compiled);

    private readonly FxpBackendStore _store;

    public ReleaseSearchService(FxpBackendStore store)
    {
        _store = store;
    }

    public IReadOnlyList<FxpBackend> LoadServers() => _store.LoadActiveServers();

    public async Task<IReadOnlyList<ReleaseSearchSite>> FetchSitesAsync(FxpBackend server)
    {
        var password = DecryptPassword(server);
        var result = await FxpBackendSync.FetchSitesFromFxpBackend(server.Host ?? "", server.Port ?? "", password);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.ErrorMessage);

        return result.Sites
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => new ReleaseSearchSite(
                s.Name,
                (s.Sections ?? new List<FxpBackendSection>())
                    .Select(ToSuggestion)
                    .Where(section => !string.IsNullOrWhiteSpace(section.Name) || !string.IsNullOrWhiteSpace(section.Path))
                    .GroupBy(section => section.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .OrderBy(section => section.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ReleaseSearchResponse> SearchAsync(
        FxpBackend server,
        string query,
        IReadOnlyList<string> sites)
    {
        query = (query ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query))
            throw new InvalidOperationException("Enter a release name or search pattern first.");

        var selectedSites = sites
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (selectedSites.Count == 0)
            throw new InvalidOperationException("Select at least one site to search.");

        var fxpBackendQueries = BuildFxpBackendSearchQueries(query);

        using var client = CreateClient(server, 90);
        var endpoint = FxpClientService.BuildEndpoint(server);

        var logs = new List<string>();
        var results = new List<ReleaseSearchResult>();
        if (fxpBackendQueries.Count > 1 || !string.Equals(fxpBackendQueries[0], query, StringComparison.Ordinal))
            logs.Add($"Wildcard search: trying {string.Join(", ", fxpBackendQueries.Select(q => $"'{q}'"))}; filtering locally with '{query}'.");

        foreach (var fxpBackendQuery in fxpBackendQueries)
        {
            var payload = new { command = $"SITE SEARCH {fxpBackendQuery}", sites = selectedSites };
            using var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");

            var response = await client.PostAsync($"{endpoint}/raw", content);
            var text = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase} {text}");

            var outputs = ExtractSiteOutputs(text, selectedSites).ToList();

            if (outputs.Count == 0)
                outputs.Add(new SearchSiteOutput("FXP backend", text, true));

            foreach (var output in outputs)
            {
                var parsed = ParseOutput(output.Site, output.Text, query).ToList();
                var prefix = fxpBackendQueries.Count > 1 ? $"{output.Site} [{fxpBackendQuery}]" : output.Site;
                if (!output.Success)
                    logs.Add($"{prefix}: {TrimLine(output.Text, 180)}");
                else if (parsed.Count == 0)
                    logs.Add($"{prefix}: no matches");
                else
                    logs.Add($"{prefix}: {parsed.Count} match(es)");

                results.AddRange(parsed);
            }
        }

        var deduped = results
            .GroupBy(r => $"{r.Site}\u0000{r.Path}\u0000{r.Release}", StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Site, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Section, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Release, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ReleaseSearchResponse(deduped, logs);
    }

    public async Task<FxpOperationResult> QueueFxpAsync(
        FxpBackend server,
        ReleaseSearchResult result,
        string destinationSite,
        string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationSite))
            throw new InvalidOperationException("Choose a destination site.");

        var releaseName = !string.IsNullOrWhiteSpace(result.Release)
            ? result.Release
            : FxpClientService.LeafName(result.Path);

        if (string.IsNullOrWhiteSpace(releaseName))
            throw new InvalidOperationException("The selected result has no release name.");

        var sourcePath = FxpClientService.NormalizePath(result.Path);
        var parentPath = sourcePath == "/" ? result.Section : FxpClientService.ParentPath(sourcePath);
        var sourceIsSection = string.IsNullOrWhiteSpace(result.Path) || result.Path == "/" || !result.Path.StartsWith("/", StringComparison.Ordinal);

        if (string.IsNullOrWhiteSpace(parentPath))
            parentPath = result.Section;

        if (string.IsNullOrWhiteSpace(parentPath))
            throw new InvalidOperationException("The selected result has no source path or section.");

        destinationPath = FxpClientService.NormalizePath(destinationPath);
        var password = DecryptPassword(server);

        var transfer = await FxpBackendRacer.StartTransferJobFxp(
            srcSite: result.Site,
            srcSectionOrPath: parentPath,
            srcIsSection: sourceIsSection,
            dstSite: destinationSite,
            dstPath: destinationPath,
            releaseName: releaseName,
            host: server.Host ?? "",
            port: server.Port ?? "",
            password: password,
            serverName: server.Name ?? server.Id ?? "FXP backend");

        var logs = new List<string>();
        if (transfer.Success)
            logs.Add($"Queued {releaseName}: {result.Site}:{parentPath} -> {destinationSite}:{destinationPath}");
        else
            logs.Add($"Failed {releaseName}: {transfer.ErrorMessage}");

        return new FxpOperationResult(
            transfer.Success,
            transfer.Success ? "FXP queued." : transfer.ErrorMessage ?? "FXP failed.",
            logs,
            transfer.Success ? new[] { releaseName } : Array.Empty<string>());
    }

    private static IEnumerable<ReleaseSearchResult> ParseOutput(string site, string raw, string query)
    {
        var index = 0;
        foreach (var line in CleanRawLines(raw))
        {
            if (IsNoiseLine(line))
                continue;

            var path = ExtractResultPath(line, query);
            var release = !string.IsNullOrWhiteSpace(path)
                ? FxpClientService.LeafName(path)
                : ExtractRelease(line);

            if (string.IsNullOrWhiteSpace(release) || !MatchesSearch(release, query))
                continue;

            var section = !string.IsNullOrWhiteSpace(path)
                ? FirstPathSegment(path)
                : ExtractSection(line);

            yield return new ReleaseSearchResult(
                Site: site,
                Section: section,
                Release: release,
                Path: path,
                RawLine: line,
                Key: $"{site}|{path}|{release}|{index++}");
        }
    }

    private static IEnumerable<SearchSiteOutput> ExtractSiteOutputs(string text, IReadOnlyList<string> fallbackSites)
    {
        if (TryParseJson(text, out var root))
        {
            foreach (var output in ExtractJsonOutputs(root))
                yield return output;
            yield break;
        }

        if (fallbackSites.Count == 1)
        {
            yield return new SearchSiteOutput(fallbackSites[0], text, true);
            yield break;
        }

        yield return new SearchSiteOutput("FXP backend", text, true);
    }

    private static IEnumerable<SearchSiteOutput> ExtractJsonOutputs(JToken root)
    {
        if (root is JObject obj)
        {
            foreach (var failure in AsArray(obj["failures"]))
            {
                var site = Value(failure, "name", "site", "sitename") ?? "FXP backend";
                var result = Value(failure, "result", "response", "output", "message", "error") ?? failure.ToString(Formatting.None);
                yield return new SearchSiteOutput(site, result, false);
            }

            foreach (var success in AsArray(obj["successes"]))
            {
                var site = Value(success, "name", "site", "sitename") ?? "FXP backend";
                var result = Value(success, "result", "response", "output", "message") ?? success.ToString(Formatting.None);
                yield return new SearchSiteOutput(site, result, true);
            }

            if (obj["result"] is not null || obj["output"] is not null || obj["response"] is not null)
            {
                var site = Value(obj, "name", "site", "sitename") ?? "FXP backend";
                var result = Value(obj, "result", "response", "output", "message") ?? obj.ToString(Formatting.None);
                yield return new SearchSiteOutput(site, result, true);
            }

            yield break;
        }

        if (root is JArray array)
        {
            foreach (var item in array)
            {
                var site = Value(item, "name", "site", "sitename") ?? "FXP backend";
                var result = Value(item, "result", "response", "output", "message") ?? item.ToString(Formatting.None);
                yield return new SearchSiteOutput(site, result, true);
            }
        }
    }

    private static IEnumerable<JToken> AsArray(JToken? token)
    {
        if (token is JArray array)
            return array;

        return Array.Empty<JToken>();
    }

    private static string? Value(JToken token, params string[] keys)
    {
        if (token is not JObject obj)
            return token.Type == JTokenType.String ? token.Value<string>() : null;

        foreach (var key in keys)
        {
            if (obj[key] is { } value)
                return TokenText(value);
        }

        return null;
    }

    private static string? TokenText(JToken value)
    {
        if (value.Type == JTokenType.String)
            return value.Value<string>();

        if (value is JArray array)
            return string.Join("\n", array.Select(v => TokenText(v)).Where(v => !string.IsNullOrWhiteSpace(v)));

        if (value is JObject obj)
            return Value(obj, "result", "response", "output", "message", "error") ?? obj.ToString(Formatting.None);

        return value.ToString(Formatting.None);
    }

    private static bool TryParseJson(string text, out JToken root)
    {
        try
        {
            root = JToken.Parse(text);
            return true;
        }
        catch
        {
            root = JValue.CreateNull();
            return false;
        }
    }

    private static IEnumerable<string> CleanRawLines(string raw)
    {
        foreach (var original in (raw ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = StripFtpPrefix(original.Trim());
            if (!string.IsNullOrWhiteSpace(line))
                yield return line;
        }
    }

    private static string StripFtpPrefix(string line)
    {
        while (line.StartsWith("200-", StringComparison.Ordinal) || line.StartsWith("200 ", StringComparison.Ordinal))
            line = line.Length > 4 ? line[4..].TrimStart() : "";

        return line;
    }

    private static bool IsNoiseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return true;

        var lowered = line.Trim().ToLowerInvariant();
        return lowered is "200" or "command successful" or "command successful."
            || lowered.StartsWith("site search ", StringComparison.Ordinal)
            || lowered.StartsWith("doing case-insensitive search", StringComparison.Ordinal)
            || lowered.StartsWith("doing case-sensitive search", StringComparison.Ordinal)
            || lowered.StartsWith("searching ", StringComparison.Ordinal)
            || lowered.StartsWith("found ", StringComparison.Ordinal)
            || lowered.StartsWith("matches found", StringComparison.Ordinal)
            || lowered.Contains("no match", StringComparison.Ordinal)
            || lowered.Contains("nothing found", StringComparison.Ordinal);
    }

    private static string ExtractResultPath(string line, string query)
    {
        foreach (Match match in PathRegex.Matches(line))
        {
            var path = match.Groups["path"].Value.Trim().TrimEnd('.', ',', ';', ':', ')', ']');
            path = FxpClientService.NormalizePath(path);

            if (IsResultPath(path, query))
                return path;
        }

        return "";
    }

    private static bool IsResultPath(string path, string query)
    {
        path = FxpClientService.NormalizePath(path);
        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        var release = parts[^1];
        if (!MatchesSearch(release, query))
            return false;

        return ReleaseLikeRegex.IsMatch(release);
    }

    private static string ExtractRelease(string line)
    {
        var match = ReleaseLikeRegex.Match(line);
        return match.Success ? match.Groups["release"].Value.Trim() : "";
    }

    private static bool MatchesSearch(string candidate, string query)
    {
        candidate = NormalizeSearchText(candidate);
        query = NormalizeSearchText(query);

        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(query))
            return false;

        if (query.Contains('*', StringComparison.Ordinal) || query.Contains('?', StringComparison.Ordinal))
        {
            var pattern = Regex.Escape(query)
                .Replace("\\*", ".*", StringComparison.Ordinal)
                .Replace("\\?", ".", StringComparison.Ordinal);
            return Regex.IsMatch(candidate, pattern, RegexOptions.IgnoreCase);
        }

        return candidate.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> BuildFxpBackendSearchQueries(string query)
    {
        query = NormalizeSearchText(query);
        if (string.IsNullOrWhiteSpace(query))
            return Array.Empty<string>();

        if (!query.Contains('*', StringComparison.Ordinal) && !query.Contains('?', StringComparison.Ordinal))
            return new[] { query };

        var literals = Regex.Split(query, @"[\*\?]+")
            .Select(part => part.Trim())
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (literals.Count == 0)
            throw new InvalidOperationException("Wildcard search needs at least one normal character.");

        var searchable = literals
            .Where(part => part.Length >= 2)
            .OrderByDescending(part => part.Length)
            .ToList();
        if (searchable.Count == 0)
            searchable.Add(literals.OrderByDescending(part => part.Length).First());

        return searchable
            .Where(q => !string.IsNullOrWhiteSpace(q))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    private static string NormalizeSearchText(string text) =>
        (text ?? "").Trim().Trim('\'', '"');

    private static ReleaseSearchSection ToSuggestion(FxpBackendSection section)
    {
        var name = (section.Name ?? "").Trim();
        var path = FxpClientService.NormalizePath(string.IsNullOrWhiteSpace(section.Path) ? name : section.Path);
        return new ReleaseSearchSection(name, path);
    }

    private static string ExtractSection(string line)
    {
        var match = Regex.Match(line, @"\[(?<section>[^\]]+)\]");
        return match.Success ? match.Groups["section"].Value.Trim() : "";
    }

    private static string FirstPathSegment(string path)
    {
        path = FxpClientService.NormalizePath(path).Trim('/');
        if (string.IsNullOrWhiteSpace(path))
            return "";

        var idx = path.IndexOf('/');
        return idx < 0 ? path : path[..idx];
    }

    private static string TrimLine(string text, int max)
    {
        text = string.Join(" ", CleanRawLines(text)).Trim();
        return text.Length <= max ? text : text[..max] + "...";
    }

    private static string DecryptPassword(FxpBackend server) =>
        string.IsNullOrWhiteSpace(server.Password) ? "" : SecureConfig.Decrypt(server.Password);

    private static HttpClient CreateClient(FxpBackend server, int timeoutSeconds)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };

        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        var authBytes = Encoding.UTF8.GetBytes(":" + DecryptPassword(server));
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private sealed record SearchSiteOutput(string Site, string Text, bool Success);
}

public sealed record ReleaseSearchResponse(
    IReadOnlyList<ReleaseSearchResult> Results,
    IReadOnlyList<string> Logs);

public sealed record ReleaseSearchSite(
    string Name,
    IReadOnlyList<ReleaseSearchSection> Sections);

public sealed record ReleaseSearchSection(
    string Name,
    string Path);

public sealed record ReleaseSearchResult(
    string Site,
    string Section,
    string Release,
    string Path,
    string RawLine,
    string Key);
