using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RaceTrade.Web.Services;

public sealed class FxpClientService
{
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nfo", ".sfv", ".txt", ".diz", ".log", ".m3u", ".md", ".xml", ".json", ".ini", ".cfg"
    };

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp"
    };

    private const long MaxTextFileBytes = 2 * 1024 * 1024;
    private const long MaxImageFileBytes = 15 * 1024 * 1024;

    private readonly CbftpStore _store;

    public FxpClientService(CbftpStore store)
    {
        _store = store;
    }

    public IReadOnlyList<CbftpServer> LoadServers() => _store.Load().CbftpServers;

    public async Task<IReadOnlyList<string>> FetchSitesAsync(CbftpServer server)
    {
        var password = DecryptPassword(server);
        var result = await CbftpSync.FetchSitesFromCbftp(server.Host ?? "", server.Port ?? "", password);
        if (!result.IsSuccess)
            throw new InvalidOperationException(result.ErrorMessage);

        return result.Sites
            .Select(s => s.Name)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public async Task<IReadOnlyList<FxpFileItem>> BrowseAsync(CbftpServer server, string siteName, string path)
    {
        path = NormalizePath(path);

        using var client = CreateClient(server, 60);
        var endpoint = BuildEndpoint(server);
        var url = $"{endpoint}/path?site={Uri.EscapeDataString(siteName)}&path={Uri.EscapeDataString(path)}&timeout=60";

        var response = await client.GetAsync(url);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase} {text}");

        var root = JToken.Parse(text);
        var entries = ExtractEntriesArray(root);
        if (entries is null)
            return Array.Empty<FxpFileItem>();

        return entries
            .OfType<JObject>()
            .Select(e => ParseEntry(e, path))
            .Where(f => f is not null && f.Name is not "." and not "..")
            .Select(f => f!)
            .OrderBy(f => !f.IsDirectory)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<FxpOperationResult> QueueTransferAsync(
        CbftpServer server,
        string sourceSite,
        string sourcePath,
        string destinationSite,
        string destinationPath,
        IEnumerable<FxpFileItem> items)
    {
        sourcePath = NormalizePath(sourcePath);
        destinationPath = NormalizePath(destinationPath);
        var password = DecryptPassword(server);
        var logs = new List<string>();
        var queued = new List<string>();
        var success = 0;
        var failed = 0;

        foreach (var item in items.Where(i => i.Name != ".."))
        {
            var sourceItemPath = ResolveItemPath(sourcePath, item);
            var releaseName = LeafName(sourceItemPath);
            var parentPath = ParentPath(sourceItemPath);

            if (string.IsNullOrWhiteSpace(releaseName))
                releaseName = item.Name;

            var result = await CbftpRacer.StartTransferJobFxp(
                srcSite: sourceSite,
                srcSectionOrPath: parentPath,
                srcIsSection: false,
                dstSite: destinationSite,
                dstPath: destinationPath,
                releaseName: releaseName,
                host: server.Host ?? "",
                port: server.Port ?? "",
                password: password,
                serverName: server.Name ?? server.Id ?? "cbftp");

            if (result.Success)
            {
                success++;
                queued.Add(releaseName);
                logs.Add($"Queued {releaseName}: {sourceSite}:{parentPath} -> {destinationSite}:{destinationPath}");
            }
            else
            {
                failed++;
                logs.Add($"Failed {releaseName}: {result.ErrorMessage}");
            }
        }

        return new FxpOperationResult(
            failed == 0,
            $"FXP queued: {success}, failed: {failed}",
            logs,
            queued);
    }

    /// <summary>
    /// Asks cbftp how a queued job is doing.
    ///
    /// "Queued" only means cbftp accepted the POST — it says nothing about whether the
    /// transfer ran. The WinForms client polled this and reported DONE/FAILED/RUNNING;
    /// the web client did not, which is why a job that never moved looked identical to
    /// one that finished.
    ///
    /// Returns null when cbftp does not know the job (404) or cannot be reached.
    /// </summary>
    public async Task<FxpJobProgress?> GetJobProgressAsync(string releaseName)
    {
        try
        {
            var stats = await CbftpRacer.GetTransferJobStats(releaseName);
            if (stats is null)
                return null;

            return new FxpJobProgress(
                releaseName,
                stats.Status ?? "UNKNOWN",
                stats.BytesTransferred,
                stats.FilesTransferred,
                stats.FilesTotal,
                stats.AverageSpeed,
                stats.TimeElapsed,
                stats.SpeedFromApi);
        }
        catch
        {
            return null;
        }
    }

    public static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        var order = 0;

        while (len >= 1024 && order < units.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return $"{len:0.##} {units[order]}";
    }

    public async Task<FxpOperationResult> DeleteAsync(CbftpServer server, string siteName, string currentPath, IEnumerable<FxpFileItem> items)
    {
        currentPath = NormalizePath(currentPath);
        var logs = new List<string>();
        var success = 0;
        var failed = 0;

        using var client = CreateClient(server, 60);
        var endpoint = BuildEndpoint(server);

        foreach (var item in items.Where(i => i.Name != ".."))
        {
            var fullPath = item.IsSymlink
                ? CombinePath(currentPath, item.Name)
                : ResolveItemPath(currentPath, item);

            var url = $"{endpoint}/path?site={Uri.EscapeDataString(siteName)}&path={Uri.EscapeDataString(fullPath)}&type=OWN";
            var response = await client.DeleteAsync(url);
            var text = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                success++;
                logs.Add($"Deleted {siteName}:{fullPath}");
            }
            else
            {
                failed++;
                logs.Add($"Delete failed {siteName}:{fullPath}: HTTP {(int)response.StatusCode} {text}");
            }
        }

        return new FxpOperationResult(
            failed == 0,
            $"Deleted: {success}, failed: {failed}",
            logs);
    }

    public async Task<FxpViewedFile> ReadFileAsync(CbftpServer server, string siteName, string filePath, string fileName)
    {
        filePath = NormalizePath(filePath);
        var extension = Path.GetExtension(fileName);
        var isText = TextExtensions.Contains(extension);
        var isImage = ImageExtensions.Contains(extension);

        if (!isText && !isImage)
        {
            throw new InvalidOperationException(
                $"File type '{extension}' is not supported. Text: {string.Join(", ", TextExtensions)}. Images: {string.Join(", ", ImageExtensions)}.");
        }

        var maxBytes = isImage ? MaxImageFileBytes : MaxTextFileBytes;
        using var client = CreateClient(server, 60);
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

        var endpoint = BuildEndpoint(server);
        var url = $"{endpoint}/file?site={Uri.EscapeDataString(siteName)}&path={Uri.EscapeDataString(filePath)}&timeout=30";
        var response = await client.GetAsync(url);

        var contentLength = response.Content.Headers.ContentLength;
        if (contentLength is > 0 && contentLength.Value > maxBytes)
            throw new InvalidOperationException($"File is too large to preview ({FormatSize(contentLength.Value)}).");

        var bytes = await response.Content.ReadAsByteArrayAsync();
        if (!response.IsSuccessStatusCode)
        {
            var error = DecodeUtf8(bytes);
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase} {error}");
        }

        if (bytes.LongLength > maxBytes)
            throw new InvalidOperationException($"File is too large to preview ({FormatSize(bytes.LongLength)}).");

        if (isImage)
        {
            var mime = ImageMime(extension);
            var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
            return new FxpViewedFile(fileName, filePath, FxpViewedFileKind.Image, dataUrl, null, mime);
        }

        var kind = extension.Equals(".nfo", StringComparison.OrdinalIgnoreCase) ||
                   extension.Equals(".diz", StringComparison.OrdinalIgnoreCase)
            ? FxpViewedFileKind.Nfo
            : FxpViewedFileKind.Text;

        var content = kind == FxpViewedFileKind.Nfo
            ? DecodeCp437(bytes)
            : DecodeUtf8(bytes);

        return new FxpViewedFile(fileName, filePath, kind, null, content, "text/plain");
    }

    public static bool CanView(FxpFileItem item)
    {
        if (item.IsDirectory)
            return false;

        var extension = Path.GetExtension(item.Name);
        return TextExtensions.Contains(extension) || ImageExtensions.Contains(extension);
    }

    internal static string BuildEndpoint(CbftpServer server)
    {
        var host = server.Host ?? "";
        var port = server.Port ?? "";
        return host.Contains("://", StringComparison.Ordinal)
            ? (host.EndsWith($":{port}", StringComparison.Ordinal) ? host : $"{host}:{port}")
            : $"https://{host}:{port}";
    }

    internal static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";

        path = path.Replace('\\', '/').Trim();
        if (!path.StartsWith("/", StringComparison.Ordinal))
            path = "/" + path;

        while (path.Contains("//", StringComparison.Ordinal))
            path = path.Replace("//", "/", StringComparison.Ordinal);

        return path.Length > 1 ? path.TrimEnd('/') : "/";
    }

    internal static string ParentPath(string path)
    {
        path = NormalizePath(path);
        if (path == "/")
            return "/";

        var idx = path.LastIndexOf('/');
        return idx <= 0 ? "/" : path[..idx];
    }

    internal static string CombinePath(string parent, string name)
    {
        parent = NormalizePath(parent);
        name = (name ?? "").Trim('/');
        return parent == "/" ? "/" + name : parent + "/" + name;
    }

    internal static string LeafName(string path)
    {
        path = NormalizePath(path);
        if (path == "/")
            return "";
        return path[(path.LastIndexOf('/') + 1)..];
    }

    public static string ResolveItemPath(string currentPath, FxpFileItem item)
    {
        if (item.IsSymlink && !string.IsNullOrWhiteSpace(item.LinkTarget))
            return NormalizePath(item.LinkTarget);

        if (!string.IsNullOrWhiteSpace(item.FullPath))
            return NormalizePath(item.FullPath);

        return CombinePath(currentPath, item.Name);
    }

    private static string DecodeUtf8(byte[] bytes) =>
        new UTF8Encoding(false, false).GetString(bytes).TrimStart('\uFEFF');

    private static string DecodeCp437(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i];
            chars[i] = b < 128 ? (char)b : Cp437HighMap[b - 128];
        }

        return new string(chars);
    }

    private static string ImageMime(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        ".webp" => "image/webp",
        _ => "application/octet-stream"
    };

    private static readonly char[] Cp437HighMap =
    {
        '\u00C7', '\u00FC', '\u00E9', '\u00E2', '\u00E4', '\u00E0', '\u00E5', '\u00E7',
        '\u00EA', '\u00EB', '\u00E8', '\u00EF', '\u00EE', '\u00EC', '\u00C4', '\u00C5',
        '\u00C9', '\u00E6', '\u00C6', '\u00F4', '\u00F6', '\u00F2', '\u00FB', '\u00F9',
        '\u00FF', '\u00D6', '\u00DC', '\u00A2', '\u00A3', '\u00A5', '\u20A7', '\u0192',
        '\u00E1', '\u00ED', '\u00F3', '\u00FA', '\u00F1', '\u00D1', '\u00AA', '\u00BA',
        '\u00BF', '\u2310', '\u00AC', '\u00BD', '\u00BC', '\u00A1', '\u00AB', '\u00BB',
        '\u2591', '\u2592', '\u2593', '\u2502', '\u2524', '\u2561', '\u2562', '\u2556',
        '\u2555', '\u2563', '\u2551', '\u2557', '\u255D', '\u255C', '\u255B', '\u2510',
        '\u2514', '\u2534', '\u252C', '\u251C', '\u2500', '\u253C', '\u255E', '\u255F',
        '\u255A', '\u2554', '\u2569', '\u2566', '\u2560', '\u2550', '\u256C', '\u2567',
        '\u2568', '\u2564', '\u2565', '\u2559', '\u2558', '\u2552', '\u2553', '\u256B',
        '\u256A', '\u2518', '\u250C', '\u2588', '\u2584', '\u258C', '\u2590', '\u2580',
        '\u03B1', '\u00DF', '\u0393', '\u03C0', '\u03A3', '\u03C3', '\u00B5', '\u03C4',
        '\u03A6', '\u0398', '\u03A9', '\u03B4', '\u221E', '\u03C6', '\u03B5', '\u2229',
        '\u2261', '\u00B1', '\u2265', '\u2264', '\u2320', '\u2321', '\u00F7', '\u2248',
        '\u00B0', '\u2219', '\u00B7', '\u221A', '\u207F', '\u00B2', '\u25A0', '\u00A0'
    };

    private static string DecryptPassword(CbftpServer server) =>
        string.IsNullOrWhiteSpace(server.Password) ? "" : SecureConfig.Decrypt(server.Password);

    private static HttpClient CreateClient(CbftpServer server, int timeoutSeconds)
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

    private static JArray? ExtractEntriesArray(JToken root)
    {
        if (root.Type == JTokenType.Array)
            return (JArray)root;

        if (root.Type != JTokenType.Object)
            return null;

        var obj = (JObject)root;
        foreach (var key in new[] { "entries", "files", "items", "list" })
        {
            if (obj[key] is JArray array)
                return array;
        }

        return null;
    }

    private static FxpFileItem? ParseEntry(JObject entry, string parentPath)
    {
        var name = entry.Value<string>("name");
        var fullPath =
            entry.Value<string>("path") ??
            entry.Value<string>("full_path") ??
            entry.Value<string>("fullPath") ??
            entry.Value<string>("remote_path") ??
            entry.Value<string>("remotePath");

        if (string.IsNullOrWhiteSpace(fullPath) && !string.IsNullOrWhiteSpace(name))
            fullPath = CombinePath(parentPath, name);
        else
            fullPath = NormalizePath(fullPath);

        if (!string.IsNullOrWhiteSpace(name) && name.Contains("/", StringComparison.Ordinal))
            name = LeafName(name);

        if (string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(fullPath))
            name = LeafName(fullPath);

        if (string.IsNullOrWhiteSpace(name))
            return null;

        var type = (entry.Value<string>("type") ?? "").Trim().ToUpperInvariant();
        var linkTarget =
            entry.Value<string>("link_target") ??
            entry.Value<string>("linkTarget") ??
            entry.Value<string>("target") ??
            entry.Value<string>("symlink") ??
            entry.Value<string>("link");

        var isLink = type == "LINK" || !string.IsNullOrWhiteSpace(linkTarget);
        var isDir = type is "DIR" or "DIRECTORY" or "D" || (isLink && !string.IsNullOrWhiteSpace(linkTarget));
        var modified = ParseDate(
            entry.Value<string>("last_modified") ??
            entry.Value<string>("modified") ??
            entry.Value<string>("time") ??
            entry.Value<string>("date"),
            entry.Value<long?>("timestamp"));

        return new FxpFileItem
        {
            Name = name,
            FullPath = fullPath,
            IsDirectory = isDir,
            IsSymlink = isLink,
            LinkTarget = string.IsNullOrWhiteSpace(linkTarget) ? null : linkTarget,
            Size = entry.Value<long?>("size") ?? 0,
            Modified = modified
        };
    }

    private static DateTime? ParseDate(string? value, long? timestamp)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            var formats = new[]
            {
                "yyyy-MM-dd HH:mm",
                "yyyy-MM-dd HH:mm:ss",
                "yyyy-MM-dd",
                "yyyy/MM/dd HH:mm",
                "yyyy/MM/dd HH:mm:ss",
                "yyyy/MM/dd"
            };

            if (DateTime.TryParseExact(
                    value.Trim(),
                    formats,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var exact))
                return exact;

            if (DateTime.TryParse(
                    value.Trim(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var loose))
                return loose;
        }

        if (timestamp is > 0)
        {
            try { return DateTimeOffset.FromUnixTimeSeconds(timestamp.Value).LocalDateTime; }
            catch { return null; }
        }

        return null;
    }
}

public sealed class FxpFileItem
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public bool IsDirectory { get; set; }
    public bool IsSymlink { get; set; }
    public string? LinkTarget { get; set; }
    public long Size { get; set; }
    public DateTime? Modified { get; set; }

    public string TypeText => IsSymlink ? "<LINK>" : IsDirectory ? "<DIR>" : FormatSize(Size);
    public string ModifiedText => Modified?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "";
    public string DisplayName => IsSymlink && !string.IsNullOrWhiteSpace(LinkTarget)
        ? $"{Name} -> {LinkTarget}"
        : Name;

    private static string FormatSize(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}

/// <summary>
/// Result of an FXP action. <paramref name="Queued"/> holds the job names cbftp accepted,
/// so the page can follow them to completion instead of assuming "queued" means "done".
/// </summary>
public sealed record FxpOperationResult(
    bool Success,
    string Message,
    IReadOnlyList<string> Logs,
    IReadOnlyList<string>? Queued = null);

public enum FxpViewedFileKind
{
    Text,
    Nfo,
    Image
}

public sealed record FxpViewedFile(
    string Name,
    string Path,
    FxpViewedFileKind Kind,
    string? DataUrl,
    string? Text,
    string ContentType)
{
    public bool IsImage => Kind == FxpViewedFileKind.Image;
    public bool IsNfo => Kind == FxpViewedFileKind.Nfo;
    public string ViewerTitle => IsImage ? "Image Viewer" : IsNfo ? "NFO Viewer" : "Text Viewer";
}

/// <summary>A cbftp transfer job as it currently stands.</summary>
public sealed record FxpJobProgress(
    string Name,
    string Status,
    long BytesTransferred,
    int FilesTransferred,
    int FilesTotal,
    double AverageSpeed,
    TimeSpan Elapsed,
    bool SpeedFromApi)
{
    public bool IsDone => Status.Equals("DONE", StringComparison.OrdinalIgnoreCase);

    public bool IsFailed =>
        Status.Equals("FAILED", StringComparison.OrdinalIgnoreCase) ||
        Status.Equals("TIMEOUT", StringComparison.OrdinalIgnoreCase);

    public bool IsUnknown => Status.Equals("UNKNOWN", StringComparison.OrdinalIgnoreCase);

    public bool IsFinished => IsDone || IsFailed;

    /// <summary>
    /// 0-100. Based on the file count, which is the only progress cbftp reports for a
    /// transfer job. Null when it reports nothing, so the bar can go indeterminate
    /// instead of sitting at a fake 0%.
    /// </summary>
    public int? Percent =>
        IsDone ? 100
        : FilesTotal > 0 ? (int)Math.Round(100.0 * FilesTransferred / FilesTotal)
        : null;

    /// <summary>
    /// cbftp reports whole seconds, so a transfer that took under two of them produces a
    /// wild figure (a 1.25 GB job "at 1282 MB/s"). Better to say nothing than to lie.
    /// </summary>
    public bool HasMeaningfulSpeed => AverageSpeed > 0 && Elapsed.TotalSeconds >= 2;
}
