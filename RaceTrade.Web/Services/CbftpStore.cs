using Newtonsoft.Json;

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json.Linq;

namespace RaceTrade.Web.Services;

/// <summary>
/// Reads and writes cbftp/cbftp_config.json - the web equivalent of the WinForms
/// AddCbftp form. Same file, same shape, so the WinForms build and this one stay
/// interchangeable while both exist.
/// </summary>
public sealed class CbftpStore
{
    private const string Path_ = "cbftp/cbftp_config.json";

    public Config Load()
    {
        if (!File.Exists(Path_)) return new Config();

        var cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText(Path_)) ?? new Config();
        cfg.CbftpServers ??= new List<CbftpServer>();
        cfg.Jobs ??= new JobSettings();
        return cfg;
    }

    /// <summary>
    /// Saves the config, encrypting any password still held in plaintext so a value
    /// typed into the browser never reaches disk in the clear.
    /// </summary>
    public void Save(Config cfg)
    {
        Directory.CreateDirectory("cbftp");

        foreach (var s in cfg.CbftpServers ?? new List<CbftpServer>())
            s.Password = SecureConfig.EncryptIfNeeded(s.Password);

        AtomicFile.WriteAllText(Path_, JsonConvert.SerializeObject(cfg, Formatting.Indented));

        // The racer caches server config at startup; reload so edits take effect now.
        CbftpRacer.ReloadConfiguration();
        LogManager.Success("Saved cbftp servers.");
    }

    public async Task<CbftpTestResult> TestAsync(CbftpServer server)
    {
        if (server is null)
            return CbftpTestResult.Fail("Select a CBFTP server first.");

        if (string.IsNullOrWhiteSpace(server.Host))
            return CbftpTestResult.Fail("Host is empty.");

        if (string.IsNullOrWhiteSpace(server.Port))
            return CbftpTestResult.Fail("Port is empty.");

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
                return CbftpTestResult.Fail(message, endpoint, Preview(body));
            }

            JToken root;
            try
            {
                root = JToken.Parse(body);
            }
            catch (JsonException ex)
            {
                return CbftpTestResult.Fail(
                    $"Connected in {sw.ElapsedMilliseconds} ms, but /sites did not return valid JSON: {ex.Message}",
                    endpoint,
                    Preview(body));
            }

            var siteCount = CountSites(root);
            if (siteCount < 0)
            {
                return CbftpTestResult.Fail(
                    $"Connected in {sw.ElapsedMilliseconds} ms, but /sites returned an unexpected JSON shape.",
                    endpoint,
                    Preview(body));
            }

            return new CbftpTestResult(
                true,
                $"Connected in {sw.ElapsedMilliseconds} ms. Auth OK. /sites returned {siteCount} site(s).",
                endpoint);
        }
        catch (TaskCanceledException)
        {
            sw.Stop();
            return CbftpTestResult.Fail($"Connection timed out after {sw.ElapsedMilliseconds} ms.", endpoint);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return CbftpTestResult.Fail($"{ex.GetType().Name}: {ex.Message}", endpoint);
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

public sealed record CbftpTestResult(
    bool Success,
    string Message,
    string? Endpoint = null,
    string? ResponsePreview = null)
{
    public static CbftpTestResult Fail(string message, string? endpoint = null, string? responsePreview = null) =>
        new(false, message, endpoint, responsePreview);
}
