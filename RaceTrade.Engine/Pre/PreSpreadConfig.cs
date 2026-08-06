using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace RaceTrade
{
    public class PreSpreadConfigManager
    {
        private const string ConfigFolder = "pre";
        private const string CbftpServersFile = "pre/cbftp_servers.json";
        private const string SitesFile = "pre/sites.json";

        public static void EnsureConfigDirectory()
        {
            if (!Directory.Exists(ConfigFolder))
            {
                Directory.CreateDirectory(ConfigFolder);
            }
        }

        // CBFTP Servers
        public static List<PreCbftpServer> LoadCbftpServers()
        {
            EnsureConfigDirectory();

            if (!File.Exists(CbftpServersFile))
            {
                return new List<PreCbftpServer>();
            }

            try
            {
                var json = File.ReadAllText(CbftpServersFile);
                var config = JsonConvert.DeserializeObject<PreCbftpServersConfig>(json);
                return config?.Servers ?? new List<PreCbftpServer>();
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error loading CBFTP servers: {ex.Message}");
                return new List<PreCbftpServer>();
            }
        }

        public static void SaveCbftpServers(List<PreCbftpServer> servers)
        {
            EnsureConfigDirectory();

            try
            {
                var config = new PreCbftpServersConfig { Servers = servers };
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                AtomicFile.WriteAllText(CbftpServersFile, json);
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error saving CBFTP servers: {ex.Message}");
                throw;
            }
        }

        // Sites
        public static List<PreSiteConfig> LoadSites()
        {
            EnsureConfigDirectory();

            if (!File.Exists(SitesFile))
            {
                return new List<PreSiteConfig>();
            }

            try
            {
                var json = File.ReadAllText(SitesFile);
                var config = JsonConvert.DeserializeObject<PreSitesConfig>(json);
                return config?.Sites ?? new List<PreSiteConfig>();
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error loading sites: {ex.Message}");
                return new List<PreSiteConfig>();
            }
        }

        public static void SaveSites(List<PreSiteConfig> sites)
        {
            EnsureConfigDirectory();

            try
            {
                var config = new PreSitesConfig { Sites = sites };
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                AtomicFile.WriteAllText(SitesFile, json);
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error saving sites: {ex.Message}");
                throw;
            }
        }
    }

    // Config wrapper classes
    public class PreCbftpServersConfig
    {
        [JsonProperty("cbftp_servers")]
        public List<PreCbftpServer> Servers { get; set; } = new List<PreCbftpServer>();
    }

    public class PreSitesConfig
    {
        [JsonProperty("sites")]
        public List<PreSiteConfig> Sites { get; set; } = new List<PreSiteConfig>();
    }

    // CBFTP Server model
    public class PreCbftpServer
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("port")]
        public string Port { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; } // Encrypted

        [JsonProperty("profile")]
        public string Profile { get; set; }

        public override string ToString() => Name ?? Id;
    }

    // Site configuration model (SIMPLIFIED)
    public class PreSiteConfig
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("cbftp_server_id")]
        public string CbftpServerId { get; set; }

        [JsonProperty("affil_directory")]
        public string AffilDirectory { get; set; } = "/pre";

        [JsonProperty("section")]
        public string Section { get; set; } = "DEFAULT";

        [JsonProperty("enabled")]
        public bool Enabled { get; set; } = true;

        public override string ToString() => Name;
    }

    // Distribution preview item
    public class DistributionItem
    {
        public string SiteName { get; set; }
        public string CbftpServerId { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string Section { get; set; }
        public bool IsSource { get; set; }
        public bool Enabled { get; set; }

        public override string ToString()
        {
            if (!Enabled)
                return $"{SiteName} → SKIPPED (not enabled)";

            if (IsSource)
                return $"{SiteName} → {SourcePath} (source)";

            return $"{SiteName} → {DestinationPath} ({Section})";
        }
    }
}