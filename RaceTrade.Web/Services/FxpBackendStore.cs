using Newtonsoft.Json;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RaceTrade.Web.Services;

/// <summary>
/// Reads and writes the FXP backend config. The store writes the new file name and
/// still reads the old one so existing installs are migrated on the next save.
/// </summary>
public sealed class FxpBackendStore
{
    public Config Load()
    {
        if (!FxpBackendConfigFiles.TryGetReadablePath(out var path))
            return new Config();

        var cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText(path)) ?? new Config();
        cfg.FxpBackends ??= new List<FxpBackend>();
        cfg.Jobs ??= new JobSettings();
        return cfg;
    }

    public IReadOnlyList<FxpBackend> LoadActiveServers() =>
        Load().FxpBackends.Where(IsActive).ToList();

    public static bool IsActive(FxpBackend? server) => server is not null && !server.Disabled;

    /// <summary>
    /// Saves the config, encrypting any password still held in plaintext so a value
    /// typed into the browser never reaches disk in the clear.
    /// </summary>
    public void Save(Config cfg)
    {
        Directory.CreateDirectory(FxpBackendConfigFiles.DirectoryName);

        foreach (var s in cfg.FxpBackends ?? new List<FxpBackend>())
            s.Password = SecureConfig.EncryptIfNeeded(s.Password);

        AtomicFile.WriteAllText(FxpBackendConfigFiles.Path, JsonConvert.SerializeObject(cfg, Formatting.Indented));

        // The racer caches server config at startup; reload so edits take effect now.
        FxpBackendRacer.ReloadConfiguration();
        LogManager.Success("Saved FXP backend servers.");
    }

    public async Task<FxpBackendTestResult> TestAsync(FxpBackend server)
    {
        if (server is null)
            return FxpBackendTestResult.Fail("Select an FXP backend first.");

        if (string.IsNullOrWhiteSpace(server.Host))
            return FxpBackendTestResult.Fail("Host is empty.");

        if (string.IsNullOrWhiteSpace(server.Port))
            return FxpBackendTestResult.Fail("Port is empty.");

        var endpoint = FxpClientService.BuildEndpoint(server);
        var url = $"{endpoint}/sites";
        var sw = Stopwatch.StartNew();

        try
        {
            using var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };

            var authBytes = Encoding.UTF8.GetBytes(":" + ResolvePassword(server.Password));
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await client.GetAsync(url);
            var body = await response.Content.ReadAsStringAsync();
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                var message = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase} after {sw.ElapsedMilliseconds} ms.";
                return FxpBackendTestResult.Fail(message, endpoint, Preview(body));
            }

            JToken root;
            try
            {
                root = JToken.Parse(body);
            }
            catch (JsonException ex)
            {
                return FxpBackendTestResult.Fail(
                    $"Connected in {sw.ElapsedMilliseconds} ms, but /sites did not return valid JSON: {ex.Message}",
                    endpoint,
                    Preview(body));
            }

            var siteCount = CountSites(root);
            if (siteCount < 0)
            {
                return FxpBackendTestResult.Fail(
                    $"Connected in {sw.ElapsedMilliseconds} ms, but /sites returned an unexpected JSON shape.",
                    endpoint,
                    Preview(body));
            }

            return new FxpBackendTestResult(
                true,
                $"Connected in {sw.ElapsedMilliseconds} ms. Auth OK. /sites returned {siteCount} site(s).",
                endpoint);
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return FxpBackendTestResult.Fail($"Connection timed out after {sw.ElapsedMilliseconds} ms.", endpoint);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return FxpBackendTestResult.Fail($"{ex.GetType().Name}: {ex.Message}", endpoint);
        }
    }

    private static string ResolvePassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
            return "";

        if (SecureConfig.IsEncrypted(password) || password.StartsWith("ENC:", StringComparison.OrdinalIgnoreCase))
            return SecureConfig.Decrypt(password);

        return password;
    }

    private static int CountSites(JToken root)
    {
        if (root is JArray array)
            return array.Count;

        if (root is JObject obj)
        {
            foreach (var key in new[] { "sites", "items", "entries" })
            {
                if (obj[key] is JArray nested)
                    return nested.Count;
            }
        }

        return -1;
    }

    private static string? Preview(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        var compact = body.Replace("\r", " ").Replace("\n", " ").Trim();
        return compact.Length <= 360 ? compact : compact[..360] + "...";
    }
}

public sealed record FxpBackendTestResult(
    bool Success,
    string Message,
    string? Endpoint = null,
    string? ResponsePreview = null)
{
    public static FxpBackendTestResult Fail(string message, string? endpoint = null, string? responsePreview = null) =>
        new(false, message, endpoint, responsePreview);
}
