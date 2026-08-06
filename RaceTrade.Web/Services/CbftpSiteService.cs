using System.Net.Http.Headers;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace RaceTrade.Web.Services;

public sealed class CbftpSiteService
{
    private readonly CbftpStore _store;

    public CbftpSiteService(CbftpStore store)
    {
        _store = store;
    }

    public IReadOnlyList<CbftpServer> LoadServers() => _store.Load().CbftpServers;

    public async Task<IReadOnlyList<string>> LoadSiteNamesAsync(CbftpServer server)
    {
        using var client = CreateClient(server);
        var endpoint = FxpClientService.BuildEndpoint(server);
        var response = await client.GetAsync($"{endpoint}/sites");
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase} {text}");

        return JArray.Parse(text)
            .Select(t => t?.ToString())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList()!;
    }

    public async Task<CbftpSiteEditModel> LoadSiteAsync(CbftpServer server, string siteName)
    {
        using var client = CreateClient(server);
        var endpoint = FxpClientService.BuildEndpoint(server);
        var response = await client.GetAsync($"{endpoint}/sites/{Uri.EscapeDataString(siteName)}");
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase} {text}");

        var obj = JObject.Parse(text);
        var model = New(siteName);
        model.OriginalName = obj.Value<string>("name") ?? siteName;
        model.Name = obj.Value<string>("name") ?? siteName;
        model.AddressesText = string.Join(Environment.NewLine, obj["addresses"]?.Select(a => (string?)a).Where(a => !string.IsNullOrWhiteSpace(a)) ?? Enumerable.Empty<string>());
        model.User = obj.Value<string>("user") ?? "";
        model.Password = obj.Value<string>("password") ?? "";
        model.BasePath = obj.Value<string>("base_path") ?? "/";
        model.Disabled = obj.Value<bool?>("disabled") ?? false;
        model.Priority = obj.Value<string>("priority") ?? "NORMAL";
        model.ListFrequency = obj.Value<string>("list_frequency") ?? "AUTO";
        model.MaxLogins = Clamp(obj.Value<int?>("max_logins") ?? model.MaxLogins);
        model.MaxSimUp = Clamp(obj.Value<int?>("max_sim_up") ?? model.MaxSimUp);
        model.MaxSimDown = Clamp(obj.Value<int?>("max_sim_down") ?? model.MaxSimDown);
        model.TlsMode = obj.Value<string>("tls_mode") ?? "AUTH_TLS";
        model.TransferProtocol = obj.Value<string>("transfer_protocol") ?? "IPV4_ONLY";
        model.TlsTransferPolicy = obj.Value<string>("tls_transfer_policy") ?? "PREFER_OFF";
        model.TransferSourcePolicy = obj.Value<string>("transfer_source_policy") ?? "ALLOW";
        model.TransferTargetPolicy = obj.Value<string>("transfer_target_policy") ?? "ALLOW";
        model.ListCommand = obj.Value<string>("list_command") ?? "STAT_L";
        model.MaxIdleTime = Clamp(obj.Value<int?>("max_idle_time") ?? model.MaxIdleTime);
        model.StayLoggedIn = obj.Value<bool?>("stay_logged_in") ?? false;
        model.Cepr = obj.Value<bool?>("cepr") ?? false;
        model.Sscn = obj.Value<bool?>("sscn") ?? false;
        model.Cpsv = obj.Value<bool?>("cpsv") ?? false;
        model.BrokenPasv = obj.Value<bool?>("broken_pasv") ?? false;
        model.ForceBinaryMode = obj.Value<bool?>("force_binary_mode") ?? false;
        model.LeaveFreeSlot = obj.Value<bool?>("leave_free_slot") ?? false;
        model.Pret = obj.Value<bool?>("pret") ?? false;
        model.Xdupe = obj.Value<bool?>("xdupe") ?? false;
        model.AllowDownload = obj.Value<string>("allow_download") ?? "YES";
        model.AllowUpload = obj.Value<string>("allow_upload") ?? "YES";
        model.ProxyType = obj.Value<string>("proxy_type") ?? "GLOBAL";
        model.ProxyName = obj.Value<string>("proxy_name") ?? "";
        model.Affils = obj["affils"]?
            .Select(a => a?.ToString())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a!)
            .ToList() ?? new();
        model.Sections = obj["sections"]?
            .OfType<JObject>()
            .Select(s => new CbftpSiteSection
            {
                Name = s.Value<string>("name") ?? "",
                Path = s.Value<string>("path") ?? ""
            })
            .Where(s => !string.IsNullOrWhiteSpace(s.Name))
            .ToList() ?? new();
        model.Skiplist = obj["skiplist"]?
            .OfType<JObject>()
            .Select(s => new CbftpSkiplistEntry
            {
                Action = s.Value<string>("action") ?? "DENY",
                Scope = s.Value<string>("scope") ?? "ALL",
                Dir = s.Value<bool?>("dir") ?? false,
                File = s.Value<bool?>("file") ?? true,
                Regex = s.Value<bool?>("regex") ?? false,
                Pattern = s.Value<string>("pattern") ?? ""
            })
            .Where(s => !string.IsNullOrWhiteSpace(s.Pattern))
            .ToList() ?? new();

        return model;
    }

    public async Task SaveSiteAsync(CbftpServer server, CbftpSiteEditModel model)
    {
        if (string.IsNullOrWhiteSpace(model.Name))
            throw new InvalidOperationException("CBFTP site name is required.");

        var addresses = SplitLines(model.AddressesText);
        if (addresses.Count == 0)
            throw new InvalidOperationException("At least one address is required.");

        var body = new
        {
            name = model.Name.Trim(),
            addresses,
            affils = model.Affils.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).ToList(),
            allow_download = model.AllowDownload,
            allow_upload = model.AllowUpload,
            base_path = string.IsNullOrWhiteSpace(model.BasePath) ? "/" : model.BasePath.Trim(),
            broken_pasv = model.BrokenPasv,
            cepr = model.Cepr,
            cpsv = model.Cpsv,
            disabled = model.Disabled,
            force_binary_mode = model.ForceBinaryMode,
            leave_free_slot = model.LeaveFreeSlot,
            list_command = model.ListCommand,
            max_idle_time = model.MaxIdleTime,
            max_logins = model.MaxLogins,
            max_sim_up = model.MaxSimUp,
            max_sim_down = model.MaxSimDown,
            password = model.Password ?? "",
            pret = model.Pret,
            priority = model.Priority,
            list_frequency = model.ListFrequency,
            proxy_name = model.ProxyName?.Trim() ?? "",
            proxy_type = model.ProxyType,
            sections = model.Sections
                .Where(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.Path))
                .Select(s => new { name = s.Name.Trim(), path = s.Path.Trim() })
                .ToList(),
            skiplist = model.Skiplist
                .Where(s => !string.IsNullOrWhiteSpace(s.Pattern))
                .Select(s => new { action = s.Action, dir = s.Dir, file = s.File, pattern = s.Pattern.Trim(), regex = s.Regex, scope = s.Scope })
                .ToList(),
            sscn = model.Sscn,
            stay_logged_in = model.StayLoggedIn,
            tls_mode = model.TlsMode,
            tls_transfer_policy = model.TlsTransferPolicy,
            transfer_protocol = model.TransferProtocol,
            transfer_source_policy = model.TransferSourcePolicy,
            transfer_target_policy = model.TransferTargetPolicy,
            user = model.User?.Trim() ?? "",
            xdupe = model.Xdupe
        };

        using var client = CreateClient(server);
        var endpoint = FxpClientService.BuildEndpoint(server);
        var json = JsonConvert.SerializeObject(body, Formatting.Indented);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");

        HttpResponseMessage response;
        if (string.IsNullOrWhiteSpace(model.OriginalName))
        {
            response = await client.PostAsync($"{endpoint}/sites", content);
        }
        else
        {
            var request = new HttpRequestMessage(
                new HttpMethod("PATCH"),
                $"{endpoint}/sites/{Uri.EscapeDataString(model.OriginalName)}")
            {
                Content = content
            };
            response = await client.SendAsync(request);
        }

        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase} {text}");
    }

    public async Task<string> GetSiteRulesAsync(CbftpServer server, string siteName)
    {
        using var client = CreateClient(server);
        var endpoint = FxpClientService.BuildEndpoint(server);
        var payload = new { command = "SITE RULES", sites = new[] { siteName } };
        using var content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json");
        var response = await client.PostAsync($"{endpoint}/raw", content);
        var text = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return $"SITE RULES failed ({(int)response.StatusCode} {response.ReasonPhrase})\r\n\r\n{text}";

        try
        {
            var obj = JObject.Parse(text);
            var raw = obj["successes"]?.FirstOrDefault()?["result"]?.ToString();
            return CleanRulesText(string.IsNullOrWhiteSpace(raw) ? text : raw);
        }
        catch
        {
            return CleanRulesText(text);
        }
    }

    public static CbftpSiteEditModel New(string name = "") => new()
    {
        Name = name,
        BasePath = "/",
        Priority = "NORMAL",
        ListFrequency = "AUTO",
        MaxLogins = 2,
        MaxSimUp = 3,
        MaxSimDown = 2,
        TlsMode = "AUTH_TLS",
        TransferProtocol = "IPV4_ONLY",
        TlsTransferPolicy = "PREFER_OFF",
        TransferSourcePolicy = "ALLOW",
        TransferTargetPolicy = "ALLOW",
        ListCommand = "STAT_L",
        MaxIdleTime = 60,
        AllowDownload = "YES",
        AllowUpload = "YES",
        ProxyType = "GLOBAL"
    };

    private static HttpClient CreateClient(CbftpServer server)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        var password = string.IsNullOrWhiteSpace(server.Password) ? "" : SecureConfig.Decrypt(server.Password);
        var authBytes = Encoding.UTF8.GetBytes(":" + password);
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static List<string> SplitLines(string? value) =>
        (value ?? "")
            .Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0)
            .ToList();

    private static int Clamp(int value) => Math.Max(0, Math.Min(value, 100000));

    private static string CleanRulesText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            var line = raw;
            if (line.Contains("Command Successful", StringComparison.OrdinalIgnoreCase))
                continue;

            if (line.StartsWith("200- ", StringComparison.Ordinal)) line = line[5..];
            else if (line.StartsWith("200-", StringComparison.Ordinal)) line = line[4..];
            else if (line.StartsWith("200 ", StringComparison.Ordinal)) line = line[4..];
            else if (line.StartsWith("200", StringComparison.Ordinal)) line = line[3..];

            sb.AppendLine(line);
        }

        return sb.ToString().Trim();
    }
}

public sealed class CbftpSiteEditModel
{
    public string? OriginalName { get; set; }
    public string Name { get; set; } = "";
    public string AddressesText { get; set; } = "";
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string BasePath { get; set; } = "/";
    public bool Disabled { get; set; }
    public string Priority { get; set; } = "NORMAL";
    public string ListFrequency { get; set; } = "AUTO";
    public int MaxLogins { get; set; }
    public int MaxSimUp { get; set; }
    public int MaxSimDown { get; set; }
    public string TlsMode { get; set; } = "AUTH_TLS";
    public string TransferProtocol { get; set; } = "IPV4_ONLY";
    public string TlsTransferPolicy { get; set; } = "PREFER_OFF";
    public string TransferSourcePolicy { get; set; } = "ALLOW";
    public string TransferTargetPolicy { get; set; } = "ALLOW";
    public string ListCommand { get; set; } = "STAT_L";
    public int MaxIdleTime { get; set; }
    public bool StayLoggedIn { get; set; }
    public bool Cepr { get; set; }
    public bool Sscn { get; set; }
    public bool Cpsv { get; set; }
    public bool BrokenPasv { get; set; }
    public bool ForceBinaryMode { get; set; }
    public bool LeaveFreeSlot { get; set; }
    public bool Pret { get; set; }
    public bool Xdupe { get; set; }
    public string AllowDownload { get; set; } = "YES";
    public string AllowUpload { get; set; } = "YES";
    public string ProxyType { get; set; } = "GLOBAL";
    public string ProxyName { get; set; } = "";
    public List<string> Affils { get; set; } = new();
    public List<CbftpSiteSection> Sections { get; set; } = new();
    public List<CbftpSkiplistEntry> Skiplist { get; set; } = new();
}

public sealed class CbftpSiteSection
{
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
}

public sealed class CbftpSkiplistEntry
{
    public string Action { get; set; } = "DENY";
    public string Scope { get; set; } = "ALL";
    public bool Dir { get; set; }
    public bool File { get; set; } = true;
    public bool Regex { get; set; }
    public string Pattern { get; set; } = "";
}
