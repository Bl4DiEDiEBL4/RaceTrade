using System.Collections.Generic;
using Newtonsoft.Json;

namespace RaceTrade
{
    /// <summary>
    /// Models for the PreBot configs under pre_bots\*.json.
    ///
    /// Like the cbftp models, these were declared inside a WinForms file (PreBot.cs) in
    /// the old build, so they came out with it during the port. They are plain data
    /// contracts consumed by the engine's PreBot handling. Property names are unchanged,
    /// so existing pre_bots\*.json files deserialize exactly as before.
    /// </summary>
    public class PreBotConfig
    {
        public ZncServerSettings ZncServer { get; set; } = new ZncServerSettings();
        public PreBotSiteSettings SiteSettings { get; set; } = new PreBotSiteSettings();
    }

    public class ZncServerSettings
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public string Username { get; set; }

        /// <summary>Stored encrypted (see <see cref="SecureConfig"/>); decrypt before use.</summary>
        public string Password { get; set; }
    }

    public class PreBotSiteSettings
    {
        public string Sitename { get; set; }
        public string BotName { get; set; }
        public string Channel1 { get; set; }

        /// <summary>Stored encrypted (see <see cref="SecureConfig"/>); decrypt before use.</summary>
        public string BlowfishKey1 { get; set; }

        public string SectionRegex { get; set; }
        public string SectionPrefix { get; set; }
        public string SectionSuffix { get; set; }
        public string NameRegex { get; set; }
    }

    /// <summary>
    /// Model for sections\cbftp_sections.json (was declared in the AddCbftpSections form).
    /// </summary>
    public class SectionData
    {
        [JsonProperty("sections")]
        public Dictionary<string, string> Sections { get; set; }

        [JsonProperty("cbftp_sections")]
        public Dictionary<string, string> CbftpSections { get; set; }
    }
}
