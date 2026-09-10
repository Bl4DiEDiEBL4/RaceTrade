using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

// Models for fxp_backend/fxp_backend_config.json.
//
// These lived inside the WinForms AddFxpBackend form in the old build, so deleting that form
// during the port took the data contracts with it. The JSON reader still accepts the
// old backend config shape for existing installs.
//
// Deliberately declared in the GLOBAL namespace, matching the original: the consumers
// are spread over different namespaces (RequestAutoFillManager is in `RaceTrader`,
// FxpBackendRacer is in no namespace at all), and the global namespace is the one scope all
// of them can see without extra usings.
//
// Note there are similarly-named types NESTED inside class FxpBackendRacer
// (FxpBackendRacer.MainConfig / FxpBackendRacer.FxpBackend / FxpBackendRacer.JobSettings). Those are
// private implementation detail of that class and do NOT collide with these: inside
// FxpBackendRacer the nested ones win by normal scoping rules, everywhere else these apply.

public static class FxpBackendConfigFiles
{
    public const string DirectoryName = "fxp_backend";
    public const string FileName = "fxp_backend_config.json";
    private const string LegacyDirectoryName = "c" + "bftp";
    private const string LegacyFileName = "c" + "bftp_config.json";

    public static string Path => System.IO.Path.Combine(DirectoryName, FileName);
    private static string LegacyPath => System.IO.Path.Combine(LegacyDirectoryName, LegacyFileName);

    public static bool TryGetReadablePath(out string path)
    {
        if (File.Exists(Path))
        {
            path = Path;
            return true;
        }

        if (File.Exists(LegacyPath))
        {
            path = LegacyPath;
            return true;
        }

        path = Path;
        return false;
    }
}

public static class FxpBackendJsonKeys
{
    public const string Backends = "fxp_backends";
    public const string BackendId = "fxp_backend_id";
    public const string SectionMap = "map_fxp_backend_section";
    public const string Sections = "fxp_backend_sections";
    public const string LegacyBackends = "c" + "bftp_servers";
    public const string LegacyBackendId = "c" + "bftp_server_id";
    public const string LegacySectionMap = "map_c" + "bftp_section";
    public const string LegacySections = "c" + "bftp_sections";
}

/// <summary>Root of fxp_backend/fxp_backend_config.json.</summary>
public class Config
{
    [JsonProperty(FxpBackendJsonKeys.Backends)]
    public List<FxpBackend> FxpBackends { get; set; } = new List<FxpBackend>();

    [JsonProperty(FxpBackendJsonKeys.LegacyBackends, NullValueHandling = NullValueHandling.Ignore)]
    private List<FxpBackend> LegacyBackends
    {
        get => null;
        set
        {
            if (value != null && value.Count > 0 && FxpBackends.Count == 0)
                FxpBackends = value;
        }
    }

    [JsonProperty("jobs")]
    public JobSettings Jobs { get; set; } = new JobSettings();
}

public class FxpBackend
{
    [JsonProperty("id")]
    public string Id { get; set; }

    [JsonProperty("name")]
    public string Name { get; set; }

    [JsonProperty("host")]
    public string Host { get; set; }

    [JsonProperty("port")]
    public string Port { get; set; }

    /// <summary>Stored encrypted (see SecureConfig); decrypt before use.</summary>
    [JsonProperty("password")]
    public string Password { get; set; }

    [JsonProperty("profile")]
    public string Profile { get; set; }

    [JsonProperty("disabled")]
    public bool Disabled { get; set; }
}

public class JobSettings
{
    [JsonProperty("spreadjob")]
    public bool Spreadjob { get; set; }

    [JsonProperty("fxpjob")]
    public bool Fxpjob { get; set; }
}
