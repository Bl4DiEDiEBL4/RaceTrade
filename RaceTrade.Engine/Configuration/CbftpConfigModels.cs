using System.Collections.Generic;
using Newtonsoft.Json;

// Models for cbftp/cbftp_config.json.
//
// These lived inside the WinForms AddCbftp form in the old build, so deleting that form
// during the port took the data contracts with it.
//
// Deliberately declared in the GLOBAL namespace, matching the original: the consumers
// are spread over different namespaces (RequestAutoFillManager is in `RaceTrader`,
// CbftpRacer is in no namespace at all), and the global namespace is the one scope all
// of them can see without extra usings.
//
// Note there are similarly-named types NESTED inside class CbftpRacer
// (CbftpRacer.MainConfig / CbftpRacer.CbftpServer / CbftpRacer.JobSettings). Those are
// private implementation detail of that class and do NOT collide with these: inside
// CbftpRacer the nested ones win by normal scoping rules, everywhere else these apply.

/// <summary>Root of cbftp/cbftp_config.json.</summary>
public class Config
{
    [JsonProperty("cbftp_servers")]
    public List<CbftpServer> CbftpServers { get; set; } = new List<CbftpServer>();

    [JsonProperty("jobs")]
    public JobSettings Jobs { get; set; } = new JobSettings();
}

public class CbftpServer
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
}

public class JobSettings
{
    [JsonProperty("spreadjob")]
    public bool Spreadjob { get; set; }

    [JsonProperty("fxpjob")]
    public bool Fxpjob { get; set; }
}
