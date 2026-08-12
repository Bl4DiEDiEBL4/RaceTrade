using Newtonsoft.Json;

namespace RaceTrade.Engine.Transfer
{
    /// <summary>How TLS is established on the control connection.</summary>
    public enum FtpTlsMode
    {
        /// <summary>Plain FTP. Still common on private sites behind a VPN.</summary>
        None = 0,

        /// <summary>Connect plain on 21, then AUTH TLS (RFC 4217). What scene sites use.</summary>
        Explicit = 1,

        /// <summary>TLS from the first byte, usually on 990. Rare on scene sites.</summary>
        Implicit = 2
    }

    /// <summary>
    /// Everything needed to talk FTP to one site. This is the config cbftp currently owns
    /// on RaceTrade's behalf; a native racer needs it locally.
    ///
    /// Stored on the site JSON under "ftp". When it is absent the site keeps racing
    /// through cbftp, so adding this model changes nothing until a profile is filled in.
    /// </summary>
    public class FtpSiteProfile
    {
        [JsonProperty("host")]
        public string Host { get; set; }

        [JsonProperty("port")]
        public int Port { get; set; } = 21

        ;

        [JsonProperty("username")]
        public string Username { get; set; }

        [JsonProperty("password")]
        public string Password { get; set; }

        [JsonProperty("tls_mode")]
        public FtpTlsMode TlsMode { get; set; } = FtpTlsMode.Explicit;

        /// <summary>
        /// PROT P (encrypted data channel) vs PROT C (clear data channel).
        ///
        /// Worth understanding before flipping this on: with PROT P, a plain LIST needs a
        /// TLS data connection, and most glftpd installs require TLS session reuse there.
        /// .NET's SslStream cannot resume a session across connections, so those listings
        /// fail with 522/425 no matter what we do. That is exactly why
        /// <see cref="PreferStatListing"/> defaults to true: STAT -l answers on the control
        /// connection and never opens a data channel. FXP is unaffected either way, because
        /// the data connection there is between the two servers, not us.
        /// </summary>
        [JsonProperty("protected_data")]
        public bool ProtectedData { get; set; } = true;

        /// <summary>
        /// Accept the site's certificate without validation. Scene sites are self-signed
        /// essentially without exception, so this defaults to true; set it false only if
        /// the site has a real chain.
        /// </summary>
        [JsonProperty("accept_any_certificate")]
        public bool AcceptAnyCertificate { get; set; } = true;

        /// <summary>
        /// Send PRET before PASV/PORT. Required by drftpd (and glftpd builds with the
        /// pret module): without it the master hands out a slave that is not the one
        /// holding the file. Autodetected from FEAT; this only forces it on.
        /// </summary>
        [JsonProperty("use_pret")]
        public bool UsePret { get; set; }

        /// <summary>
        /// Use SSCN for secure site-to-site. Autodetected from FEAT.
        /// </summary>
        [JsonProperty("use_sscn")]
        public bool UseSscn { get; set; }

        /// <summary>
        /// Flips which end of an FXP acts as the TLS client. Default (false) puts SSCN ON
        /// on the receiving site, which is the one that connects out. A handful of daemons
        /// want it the other way round.
        /// </summary>
        [JsonProperty("sscn_on_source")]
        public bool SscnOnSource { get; set; }

        /// <summary>Prefer EPSV/EPRT over PASV/PORT. Needed for IPv6.</summary>
        [JsonProperty("use_epsv")]
        public bool UseEpsv { get; set; }

        /// <summary>
        /// List with "STAT -l" instead of LIST. No data connection, so it is both faster
        /// and immune to the TLS session-reuse problem described above. glftpd, drftpd and
        /// raidenftpd all support it.
        /// </summary>
        [JsonProperty("prefer_stat_listing")]
        public bool PreferStatListing { get; set; } = true;

        /// <summary>
        /// Hard cap on simultaneous logins. Sites ban on overshooting this, so the
        /// connection pool treats it as a limit, not a target.
        /// </summary>
        [JsonProperty("max_logins")]
        public int MaxLogins { get; set; } = 3;

        [JsonProperty("max_up_slots")]
        public int MaxUpSlots { get; set; } = 2;

        [JsonProperty("max_down_slots")]
        public int MaxDownSlots { get; set; } = 2;

        /// <summary>Seconds of idle before NOOP. Most sites drop at 300.</summary>
        [JsonProperty("idle_seconds")]
        public int IdleSeconds { get; set; } = 120;

        [JsonProperty("connect_timeout_seconds")]
        public int ConnectTimeoutSeconds { get; set; } = 20;

        [JsonProperty("command_timeout_seconds")]
        public int CommandTimeoutSeconds { get; set; } = 30;

        /// <summary>
        /// Path prepended to every section path, for sites where the login lands above
        /// the section tree. Empty means the login directory is the root.
        /// </summary>
        [JsonProperty("base_path")]
        public string BasePath { get; set; } = "";

        public bool IsUsable()
        {
            return !string.IsNullOrWhiteSpace(Host)
                && Port > 0
                && !string.IsNullOrWhiteSpace(Username);
        }
    }
}
