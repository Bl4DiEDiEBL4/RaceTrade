using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

// Configuration models extracted from the WinForms MainApp.cs. They are pure
// data contracts and belong with the engine, not with a Form.
namespace RaceTrade
{
    public class SiteConfig
    {
        [JsonProperty("server")]
        public ServerSettings Server { get; set; }

        [JsonProperty("site_settings")]
        public SiteSettings SiteSettings { get; set; }

        [JsonProperty("affils")]
        public List<string> Affils { get; set; }

        [JsonProperty("race_sections_enabled")]
        public List<string> RaceSectionsEnabled { get; set; } = new();

        [JsonProperty("global_blacklist")]
        public List<string> GlobalBlacklist { get; set; } = new();

        [JsonProperty("sections")]
        public List<Section> Sections { get; set; } = new();
    }

    public class ServerSettings
    {
        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("port")]
        public int Port { get; set; }

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        // Optional explicit ZNC network name. When set it is authoritative and the
        // app connects with login "username/network", guaranteeing the JOINs land
        // on the right network. When empty, the network is parsed from a
        // "user/network" style Username for backward compatibility.
        [JsonProperty("network", NullValueHandling = NullValueHandling.Ignore)]
        public string Network { get; set; }

        // Defaults match the old WinForms app and keep existing site JSON working.
        [JsonProperty("use_tls")]
        public bool UseTls { get; set; } = true;

        [JsonProperty("use_blowfish")]
        public bool UseBlowfish { get; set; } = true;
    }

    public class SiteSettings
    {
        [JsonProperty("chat_keys")]
        public Dictionary<string, string> ChatKeys { get; set; } = new();

        [JsonProperty("pre_announce")]
        public string PreOrSite { get; set; }

        [JsonProperty("sitename")]
        public string Sitename { get; set; }

        [JsonProperty("bot_name")]
        public string BotName { get; set; }

        [JsonProperty("new_regex_pattern")]
        public string NewRegexPattern { get; set; }

        [JsonProperty("section_regex_pattern")]
        public string SectionRegexPattern { get; set; }

        [JsonProperty("release_regex_pattern")]
        public string ReleaseRegexPattern { get; set; }

        [JsonProperty("section_prefix")]
        public string SectionPrefix { get; set; }

        [JsonProperty("section_suffix")]
        public string SectionSuffix { get; set; }

        [JsonProperty("release_prefix")]
        public string ReleasePrefix { get; set; }

        [JsonProperty("release_suffix")]
        public string ReleaseSuffix { get; set; }

        [JsonProperty("ignore_words")]
        public string IgnoreWords { get; set; }

        [JsonProperty("dl_only_site")]
        public bool DlOnlySite { get; set; }

        [JsonProperty("disable_site")]
        public bool DisableSite { get; set; }

        [JsonProperty("chan1")]
        public string Chan1 { get; set; }

        [JsonProperty("blowfish_key1")]
        public string BlowfishKey1 { get; set; }

        [JsonProperty("chan2")]
        public string Chan2 { get; set; }

        [JsonProperty("blowfish_key2")]
        public string BlowfishKey2 { get; set; }

        [JsonProperty("chan3")]
        public string Chan3 { get; set; }

        [JsonProperty("blowfish_key3")]
        public string BlowfishKey3 { get; set; }

        [JsonProperty("chan4")]
        public string Chan4 { get; set; }

        [JsonProperty("blowfish_key4")]
        public string BlowfishKey4 { get; set; }

        [JsonProperty("chan5")]
        public string Chan5 { get; set; }

        [JsonProperty("blowfish_key5")]
        public string BlowfishKey5 { get; set; }

        [JsonProperty("chan6")]
        public string Chan6 { get; set; }

        [JsonProperty("blowfish_key6")]
        public string BlowfishKey6 { get; set; }

        [JsonProperty("chan7")]
        public string Chan7 { get; set; }

        [JsonProperty("blowfish_key7")]
        public string BlowfishKey7 { get; set; }

        [JsonProperty("chan8")]
        public string Chan8 { get; set; }

        [JsonProperty("blowfish_key8")]
        public string BlowfishKey8 { get; set; }

        [JsonProperty("chan9")]
        public string Chan9 { get; set; }

        [JsonProperty("blowfish_key9")]
        public string BlowfishKey9 { get; set; }

        [JsonProperty("chan10")]
        public string Chan10 { get; set; }

        [JsonProperty("blowfish_key10")]
        public string BlowfishKey10 { get; set; }

        [JsonProperty("chan11")]
        public string Chan11 { get; set; }

        [JsonProperty("blowfish_key11")]
        public string BlowfishKey11 { get; set; }

        [JsonProperty("chan12")]
        public string Chan12 { get; set; }

        [JsonProperty("blowfish_key12")]
        public string BlowfishKey12 { get; set; }

        [JsonProperty("chan13")]
        public string Chan13 { get; set; }

        [JsonProperty("blowfish_key13")]
        public string BlowfishKey13 { get; set; }

        [JsonProperty("chan14")]
        public string Chan14 { get; set; }

        [JsonProperty("blowfish_key14")]
        public string BlowfishKey14 { get; set; }

        [JsonProperty("chan15")]
        public string Chan15 { get; set; }

        [JsonProperty("blowfish_key15")]
        public string BlowfishKey15 { get; set; }

        [JsonProperty("chan16")]
        public string Chan16 { get; set; }

        [JsonProperty("blowfish_key16")]
        public string BlowfishKey16 { get; set; }

        [JsonProperty("chan17")]
        public string Chan17 { get; set; }

        [JsonProperty("blowfish_key17")]
        public string BlowfishKey17 { get; set; }

        [JsonProperty("chan18")]
        public string Chan18 { get; set; }

        [JsonProperty("blowfish_key18")]
        public string BlowfishKey18 { get; set; }

        [JsonProperty("chan19")]
        public string Chan19 { get; set; }

        [JsonProperty("blowfish_key19")]
        public string BlowfishKey19 { get; set; }

        [JsonProperty("chan20")]
        public string Chan20 { get; set; }

        [JsonProperty("blowfish_key20")]
        public string BlowfishKey20 { get; set; }

        // ─────────────────────────────────────────────────────────────
        // request-autofill template settings (no hardcoded regex)
        // ─────────────────────────────────────────────────────────────

        [JsonProperty("request_auto_fill_enabled")]
        public bool RequestAutoFillEnabled { get; set; }

        // Command to fetch requests, e.g. "SITE REQUESTS"
        [JsonProperty("request_list_command")]
        public string RequestListCommand { get; set; }

        // Regex pattern for one request line. Groups: "id", "name" (and optionally "user")
        [JsonProperty("request_line_pattern")]
        public string RequestLinePattern { get; set; }

        // How to send REQFILLED, e.g. "SITE REQFILLED {id}" or "SITE REQFILLED {name}"
        [JsonProperty("request_fill_template")]
        public string RequestFillTemplate { get; set; }

        // Where to create the transfer job dst path, e.g. "/REQUESTS/REQ-{release}"
        [JsonProperty("request_dst_path_template")]
        public string RequestDstPathTemplate { get; set; }

        // Pattern (or plain text) that marks a request as COMPLETE on the site
        [JsonProperty("request_complete_pattern")]
        public string RequestCompletePattern { get; set; }

        // How often to poll the site for requests (in seconds)
        [JsonProperty("request_poll_seconds")]
        public int RequestPollSeconds { get; set; } = 300;

        // Can this site be used as a SOURCE when filling requests for other sites?
        // (i.e. we can take releases from here to fill requests elsewhere)
        [JsonProperty("request_can_fill_source")]
        public bool RequestCanFillSource { get; set; } = false;

        [JsonProperty("incomplete_auto_fxp_enabled")]
        public bool IncompleteAutoFxpEnabled { get; set; }

        [JsonProperty("incomplete_search_source")]
        public bool IncompleteSearchSource { get; set; }

        [JsonProperty("incomplete_marker_regex")]
        public string IncompleteMarkerRegex { get; set; }

        [JsonProperty("incomplete_section_regex")]
        public string IncompleteSectionRegex { get; set; }

        [JsonProperty("incomplete_release_regex")]
        public string IncompleteReleaseRegex { get; set; }

        [JsonProperty("incomplete_section_prefix")]
        public string IncompleteSectionPrefix { get; set; }

        [JsonProperty("incomplete_section_suffix")]
        public string IncompleteSectionSuffix { get; set; }

        [JsonProperty("incomplete_ignore_words")]
        public string IncompleteIgnoreWords { get; set; }

        [JsonProperty("incomplete_search_command_template")]
        public string IncompleteSearchCommandTemplate { get; set; }

        [JsonProperty("incomplete_dst_path_template")]
        public string IncompleteDstPathTemplate { get; set; }

        [JsonProperty("pre_regex_pattern")]
        public string PreRegexPattern { get; set; }              // Pre_field_regex

        [JsonProperty("pre_section_regex_pattern")]
        public string PreSectionRegexPattern { get; set; }       // Section_pre_field

        [JsonProperty("pre_section_prefix")]
        public string PreSectionPrefix { get; set; }             // Section_pre_prefix_field

        [JsonProperty("pre_section_suffix")]
        public string PreSectionSuffix { get; set; }             // Section_pre_suffix_field

        [JsonProperty("pre_release_regex_pattern")]
        public string PreReleaseRegexPattern { get; set; }       // Release_Pre_field

        [JsonProperty("max_pre_time")]
        public int? MaxPreTime { get; set; }

    }

    public class Section
    {
        [JsonProperty("irc_name")]
        public string IrcName { get; set; }

        [JsonProperty("pretime")]
        public int? Pretime { get; set; }

        [JsonProperty("bnc")]
        public string Bnc { get; set; }

        [JsonProperty("tags")]
        public List<Tag> Tags { get; set; } = new();

        [JsonProperty("rules")]
        public List<string> Rules { get; set; } = new();

        [JsonProperty("skiplists")]
        public List<string> Skiplists { get; set; } = new();

        [JsonProperty("dupeRules")]
        public DupeRules DupeRules { get; set; }

        [JsonProperty("imdb", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Imdb { get; set; }

        [JsonProperty("tvmaze", NullValueHandling = NullValueHandling.Ignore)]
        public JObject Tvmaze { get; set; }

    }

    public class Tag
    {
        [JsonProperty("map_cbftp_section")]
        public string MapCbftpSection { get; set; }

        [JsonProperty("trigger_regex")]
        public string TriggerRegex { get; set; }

        [JsonProperty("rules")]
        public List<string> Rules { get; set; } = new();
    }

    public class DupeRules
    {
        [JsonProperty("firstWins")]
        public bool FirstWins { get; set; }

        [JsonProperty("priority")]
        public string Priority { get; set; }
    }

    public class Mapping
    {
        public string MapCbftpSection { get; set; }
        public string TriggerRegex { get; set; }
        public List<string> Rules { get; set; } = new List<string>();
}
}
