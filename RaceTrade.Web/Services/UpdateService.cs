using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace RaceTrade.Web.Services;

public sealed class UpdateService
{
    private const string SettingsFile = "settings/settings.json";
    private static readonly Uri LatestReleaseUri =
        new("https://api.github.com/repos/Bl4DiEDiEBL4/RaceTrade/releases/latest");

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateCheckResult? _cached;
    private DateTimeOffset _lastCheck = DateTimeOffset.MinValue;

    public UpdateService(HttpClient http) => _http = http;

    public event Action? Changed;

    public string CurrentVersion => ReadCurrentVersion();

    public bool CheckForUpdatesEnabled => ReadEnabledSetting();

    public void SettingsChanged()
    {
        _cached = null;
        _lastCheck = DateTimeOffset.MinValue;
        Changed?.Invoke();
    }

    public async Task<UpdateCheckResult> CheckAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var current = CurrentVersion;

        if (!CheckForUpdatesEnabled)
            return UpdateCheckResult.Disabled(current);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            await _gate.WaitAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            return UpdateCheckResult.Failed(current, ex.Message);
        }

        try
        {
            if (!force &&
                _cached is not null &&
                DateTimeOffset.Now - _lastCheck < TimeSpan.FromHours(6))
            {
                return _cached;
            }

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
                request.Headers.UserAgent.ParseAdd("RaceTrade-WebUI");
                request.Headers.Accept.ParseAdd("application/vnd.github+json");

                using var response = await _http.SendAsync(request, timeout.Token);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync(timeout.Token);
                var release = JObject.Parse(json);
                var tag = release["tag_name"]?.ToString() ?? "";
                var url = release["html_url"]?.ToString() ?? "";
                var latest = ExtractVersion(tag);

                _cached = IsNewer(latest, current)
                    ? UpdateCheckResult.Available(current, latest, url)
                    : UpdateCheckResult.Current(current, latest);
            }
            catch (Exception ex)
            {
                _cached = UpdateCheckResult.Failed(current, ex.Message);
            }

            _lastCheck = DateTimeOffset.Now;
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool ReadEnabledSetting()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return true;

            var o = JObject.Parse(File.ReadAllText(SettingsFile));
            var token = o["check_for_updates"] ?? o["CheckForUpdates"];

            if (token is null) return true;
            if (token.Type == JTokenType.Boolean) return token.Value<bool>();

            return !bool.TryParse(token.ToString(), out var enabled) || enabled;
        }
        catch
        {
            return true;
        }
    }

    private static string ReadCurrentVersion()
    {
        var info = typeof(UpdateService).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        var extracted = ExtractVersion(info);
        if (!string.IsNullOrWhiteSpace(extracted))
            return extracted;

        return typeof(UpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    }

    private static string ExtractVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var match = Regex.Match(value, @"\d+(?:\.\d+){0,3}");
        return match.Success ? match.Value : "";
    }

    private static bool IsNewer(string latest, string current)
    {
        if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current))
            return false;

        return ToVersion(latest).CompareTo(ToVersion(current)) > 0;
    }

    private static Version ToVersion(string value)
    {
        var parts = ExtractVersion(value)
            .Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, out var n) ? n : 0)
            .Take(4)
            .ToList();

        while (parts.Count < 4)
            parts.Add(0);

        return new Version(parts[0], parts[1], parts[2], parts[3]);
    }
}

public sealed record UpdateCheckResult(
    bool Enabled,
    bool IsAvailable,
    string CurrentVersion,
    string LatestVersion,
    string ReleaseUrl,
    string Error)
{
    public static UpdateCheckResult Disabled(string current) =>
        new(false, false, current, "", "", "");

    public static UpdateCheckResult Current(string current, string latest) =>
        new(true, false, current, latest, "", "");

    public static UpdateCheckResult Available(string current, string latest, string releaseUrl) =>
        new(true, true, current, latest, releaseUrl, "");

    public static UpdateCheckResult Failed(string current, string error) =>
        new(true, false, current, "", "", error);
}
