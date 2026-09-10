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
        private static readonly string FxpBackendsFile = Path.Combine(ConfigFolder, "fxp_backends.json");
        private static readonly string LegacyFxpBackendsFile = Path.Combine(ConfigFolder, "c" + "bftp_servers.json");
        private static readonly string SitesFile = Path.Combine(ConfigFolder, "sites.json");

        public static void EnsureConfigDirectory()
        {
            if (!Directory.Exists(ConfigFolder))
            {
                Directory.CreateDirectory(ConfigFolder);
            }
        }

        // FXP backend Servers
        public static List<PreFxpBackend> LoadFxpBackends()
        {
            EnsureConfigDirectory();

            var readableFile = File.Exists(FxpBackendsFile) ? FxpBackendsFile : LegacyFxpBackendsFile;
            if (!File.Exists(readableFile))
            {
                return new List<PreFxpBackend>();
            }

            try
            {
                var json = File.ReadAllText(readableFile);
                var config = JsonConvert.DeserializeObject<PreFxpBackendsConfig>(json);
                return config?.Servers ?? new List<PreFxpBackend>();
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error loading FXP backend servers: {ex.Message}");
                return new List<PreFxpBackend>();
            }
        }

        public static void SaveFxpBackends(List<PreFxpBackend> servers)
        {
            EnsureConfigDirectory();

            try
            {
                var config = new PreFxpBackendsConfig { Servers = servers };
                var json = JsonConvert.SerializeObject(config, Formatting.Indented);
                AtomicFile.WriteAllText(FxpBackendsFile, json);
            }
            catch (Exception ex)
            {
                LogManager.Error($"Error saving FXP backend servers: {ex.Message}");
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
    public class PreFxpBackendsConfig
    {
        [JsonProperty(FxpBackendJsonKeys.Backends)]
        public List<PreFxpBackend> Servers { get; set; } = new List<PreFxpBackend>();

        [JsonProperty(FxpBackendJsonKeys.LegacyBackends, NullValueHandling = NullValueHandling.Ignore)]
        private List<PreFxpBackend> LegacyServers
        {
            get => null;
            set
            {
                if (value != null && value.Count > 0 && (Servers == null || Servers.Count == 0))
                    Servers = value;
            }
        }
    }

    public class PreSitesConfig
    {
        [JsonProperty("sites")]
        public List<PreSiteConfig> Sites { get; set; } = new List<PreSiteConfig>();
    }

    // FXP backend Server model
    public class PreFxpBackend
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

        [JsonProperty(FxpBackendJsonKeys.BackendId)]
        public string FxpBackendId { get; set; }

        [JsonProperty(FxpBackendJsonKeys.LegacyBackendId, NullValueHandling = NullValueHandling.Ignore)]
        private string LegacyFxpBackendId
        {
            get => null;
            set
            {
                if (!string.IsNullOrWhiteSpace(value) && string.IsNullOrWhiteSpace(FxpBackendId))
                    FxpBackendId = value;
            }
        }

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
        public string FxpBackendId { get; set; }
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
