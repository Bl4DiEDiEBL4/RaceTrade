using Newtonsoft.Json;
using RaceTrade.Engine.Logging;
using RaceTrade.Engine.Compat;
using Newtonsoft.Json.Linq;
using RaceTrade;
using RaceTrader;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

public class IRCClient
{
    private readonly string host;
    private readonly int port;
    private readonly string username;
    private readonly string password;
    private readonly string expectedNetwork; // ZNC network to attach to ("" = legacy/plain IRC)
    private readonly string zncLoginUser;    // NICK/USER value (bare account when a network is set)
    private readonly string zncPass;         // PASS payload (account/network:password when a network is set)
    private readonly bool useBlowfish;
    private readonly string botName;
    private readonly string siteName;
    private List<string> channels;

    private readonly IIrcOutput logOutput;

    private readonly string sectionPrefix;
    private readonly string sectionSuffix;
    private readonly string releasePrefix;
    private readonly string releaseSuffix;

    private readonly Regex newFieldRegex;
    private readonly Regex sectionRegex;
    private readonly Regex releaseRegex;
    private readonly Regex preFieldRegex;
    private readonly Regex preSectionRegex;
    private readonly Regex preReleaseRegex;
    private readonly string preSectionPrefix;
    private readonly string preSectionSuffix;


    private readonly string PreOrSite;
    private List<string> ignoreWords;
    private readonly Dictionary<string, string> raceSections;
    private readonly List<string> blacklist;
    private readonly CancellationToken cancellationToken;
    private readonly Dictionary<string, FishDecryptor> fishDecryptors = new Dictionary<string, FishDecryptor>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> mappings = new Dictionary<string, string>();
    private readonly SiteConfig siteConfig;
    private readonly JObject siteConfigJson;


    private SslStream currentSslStream;


    private readonly object fishLock = new object();
    private Task listeningTask;
    private CancellationTokenSource localCancellationTokenSource;
    private TcpClient currentTcpClient;

    private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
    private int _disconnecting = 0;
    private bool IsDisconnecting => Volatile.Read(ref _disconnecting) == 1;

    // Use one lock for ALL FiSH/PM key dictionaries (not just fishDecryptors)
    private readonly object _keyLock = new object();

    public IRCClient(SiteConfig config, string siteName, IIrcOutput logOutput, CancellationToken cancellationToken)
    {
        if (config == null)
        {
            throw new ArgumentNullException(nameof(config), "Site configuration cannot be null.");
        }

        this.siteConfigJson = JObject.Parse(JsonConvert.SerializeObject(config));
        this.siteConfig = config;
        this.siteName = siteName;
        this.logOutput = logOutput;
        this.cancellationToken = cancellationToken;
        this.PreOrSite = config.SiteSettings?.PreOrSite?.Trim();

        // ---------------- SERVER SETTINGS ----------------

        if (string.IsNullOrEmpty(config.Server?.Host))
        {
            throw new ArgumentException("Host cannot be null or empty.");
        }
        this.host = config.Server.Host;
        this.useBlowfish = config.Server.UseBlowfish;

        if (config.Server.Port > 0)
        {
            this.port = config.Server.Port;
        }
        else
        {
            this.port = 6667; // Default value
        }

        if (string.IsNullOrEmpty(config.Server.Username))
        {
            throw new ArgumentException("Username cannot be null or empty.");
        }
        this.username = config.Server.Username;

        // 🔹 Allow empty password for PreBot / Global PreBot
        bool isGlobalPrebot = IsConfiguredGlobalPreBotMode(PreOrSite);
        bool isPrebot = string.Equals(PreOrSite, "PreBot", StringComparison.OrdinalIgnoreCase);

        if (!isGlobalPrebot && !isPrebot && string.IsNullOrEmpty(config.Server.Password))
        {
            // Only enforce password for normal SiteBots
            throw new ArgumentException("Password cannot be null or empty.");
        }

        if (!string.IsNullOrEmpty(config.Server.Password))
        {
            // SECURITY: Decrypt password if encrypted
            this.password = SecureConfig.Decrypt(config.Server.Password);
        }
        else
        {
            // No password (plain IRC / ZNC/psyBNC without PASS)
            this.password = string.Empty;
        }

        // Resolve the ZNC network (explicit Server.Network wins, else the
        // "/network" suffix on the Username). When a network is known we send
        // PASS as "account/network:password" and NICK/USER as the bare account so
        // ZNC attaches to the correct network. With no network we keep the legacy
        // behaviour untouched (plain IRC / single-network setups).
        this.expectedNetwork = ResolveNetwork(config.Server.Network, this.username);
        if (!string.IsNullOrWhiteSpace(this.expectedNetwork))
        {
            var account = AccountName(this.username);
            this.zncLoginUser = string.IsNullOrWhiteSpace(account) ? this.username : account;
            this.zncPass = BuildZncPassword(this.username, this.expectedNetwork, this.password);
        }
        else
        {
            this.zncLoginUser = this.username;
            this.zncPass = this.password;
        }

        // ---------------- SITE SETTINGS ----------------

        if (string.IsNullOrEmpty(config.SiteSettings?.BotName))
        {
            throw new ArgumentException("Bot name cannot be null or empty.");
        }
        this.botName = config.SiteSettings.BotName;

        // Monitor ALL configured channels (Chan1..Chan20), not just the first three.
        // The site editor supports 20 channels and LoadChannelKeys loads keys for all 20,
        // so anything past Chan3 was never joined/monitored and its messages were ignored
        // before decryption ever happened.
        this.channels = new List<string>();
        for (int i = 1; i <= 20; i++)
        {
            var chanProp = config.SiteSettings.GetType().GetProperty($"Chan{i}");
            var chanValue = (chanProp?.GetValue(config.SiteSettings) as string)?.Trim();

            if (string.IsNullOrWhiteSpace(chanValue))
                continue;

            // Normalize exactly like SetChannelKey/LoadChannelKeys do, so a channel
            // written without '#' (or with different casing) still matches.
            if (!chanValue.StartsWith("#") && !chanValue.StartsWith("PM:"))
                chanValue = "#" + chanValue.TrimStart('#');

            if (!this.channels.Contains(chanValue, StringComparer.OrdinalIgnoreCase))
                this.channels.Add(chanValue);
        }

        // Handle announce modes (Global PreBot, PreBot, SiteBot)
        if (isGlobalPrebot)
        {
            AppendOutput($"[INFO] Global PreBot detected for {siteName}. Skipping SiteBot-specific NEW validation.", Color.Cyan);

            // For Global PreBot we typically don't need NEW gating
            this.newFieldRegex = null;

            if (!string.IsNullOrEmpty(config.SiteSettings?.SectionRegexPattern))
            {
                this.sectionRegex = new Regex(config.SiteSettings.SectionRegexPattern, RegexOptions.IgnoreCase);
            }

            if (!string.IsNullOrEmpty(config.SiteSettings?.ReleaseRegexPattern))
            {
                this.releaseRegex = new Regex(config.SiteSettings.ReleaseRegexPattern, RegexOptions.IgnoreCase);
            }
        }
        else if (isPrebot)
        {
            AppendOutput($"[INFO] PreBot detected for {siteName}. Skipping strict NEW field requirement.", Color.Cyan);

            // NEW regex is optional in PreBot mode
            if (!string.IsNullOrEmpty(config.SiteSettings?.NewRegexPattern))
            {
                this.newFieldRegex = new Regex(config.SiteSettings.NewRegexPattern, RegexOptions.IgnoreCase);
            }

            if (!string.IsNullOrEmpty(config.SiteSettings?.SectionRegexPattern))
            {
                this.sectionRegex = new Regex(config.SiteSettings.SectionRegexPattern, RegexOptions.IgnoreCase);
            }

            if (!string.IsNullOrEmpty(config.SiteSettings?.ReleaseRegexPattern))
            {
                this.releaseRegex = new Regex(config.SiteSettings.ReleaseRegexPattern, RegexOptions.IgnoreCase);
            }
        }
        else
        {
            // Enforce regex validation for SiteBot
            if (string.IsNullOrEmpty(config.SiteSettings?.NewRegexPattern))
            {
                throw new ArgumentException("Missing 'new_regex_pattern' in site_settings for Site Announce mode.");
            }
            this.newFieldRegex = new Regex(config.SiteSettings.NewRegexPattern, RegexOptions.IgnoreCase);

            if (!string.IsNullOrEmpty(config.SiteSettings?.SectionRegexPattern))
            {
                this.sectionRegex = new Regex(config.SiteSettings.SectionRegexPattern, RegexOptions.IgnoreCase);
            }

            if (!string.IsNullOrEmpty(config.SiteSettings?.ReleaseRegexPattern))
            {
                this.releaseRegex = new Regex(config.SiteSettings.ReleaseRegexPattern, RegexOptions.IgnoreCase);
            }
        }

        // Normal prefixes/suffixes
        this.sectionPrefix = config.SiteSettings.SectionPrefix ?? string.Empty;
        this.sectionSuffix = config.SiteSettings.SectionSuffix ?? string.Empty;
        this.releasePrefix = config.SiteSettings.ReleasePrefix ?? string.Empty;
        this.releaseSuffix = config.SiteSettings.ReleaseSuffix ?? string.Empty;

        // 🔹 PRE / affil specific regexes (optional – used when you wire PRE handling)
        if (!string.IsNullOrEmpty(config.SiteSettings?.PreRegexPattern))
        {
            this.preFieldRegex = new Regex(config.SiteSettings.PreRegexPattern, RegexOptions.IgnoreCase);
        }

        if (!string.IsNullOrEmpty(config.SiteSettings?.PreSectionRegexPattern))
        {
            this.preSectionRegex = new Regex(config.SiteSettings.PreSectionRegexPattern, RegexOptions.IgnoreCase);
        }

        if (!string.IsNullOrEmpty(config.SiteSettings?.PreReleaseRegexPattern))
        {
            this.preReleaseRegex = new Regex(config.SiteSettings.PreReleaseRegexPattern, RegexOptions.IgnoreCase);
        }

        // 🔹 PRE / affil specific prefixes (for trimming section)
        this.preSectionPrefix = config.SiteSettings?.PreSectionPrefix ?? string.Empty;
        this.preSectionSuffix = config.SiteSettings?.PreSectionSuffix ?? string.Empty;

        // Ignore words
        this.ignoreWords = (config.SiteSettings.IgnoreWords ?? string.Empty)
            .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Trim())
            .ToList();

        // Global blacklist
        this.blacklist = config.GlobalBlacklist ?? new List<string>();

        // Race sections enabled
        this.raceSections = config.RaceSectionsEnabled?.ToDictionary(section => section, section => string.Empty)
            ?? new Dictionary<string, string>();

        // Load sections and mappings (raceSections mapping)
        if (config.Sections != null)
        {
            foreach (var section in config.Sections)
            {
                if (!string.IsNullOrEmpty(section.IrcName) && section.Tags != null)
                {
                    foreach (var tag in section.Tags)
                    {
                        if (!string.IsNullOrEmpty(tag.MapCbftpSection) && !string.IsNullOrEmpty(tag.TriggerRegex))
                        {
                            this.raceSections[tag.MapCbftpSection] = tag.TriggerRegex;
                        }
                    }
                }
            }
        }

        // Initialize Blowfish decryptors for channels
        LoadChannelKeys(config);
    }



    private void AppendOutput(string message, Color color)
    {
        if (logOutput != null && !logOutput.IsDisposed)
        {
            logOutput.AppendLog(message, color);
        }
    }

    public void Disconnect()
    {
        // Make Disconnect idempotent
        if (Interlocked.Exchange(ref _disconnecting, 1) == 1)
            return;

        try
        {
            // Cancel read loop
            try { localCancellationTokenSource?.Cancel(); } catch { }

            // Abort socket I/O. This will break SslStream.ReadAsync immediately.
            try { currentTcpClient?.Close(); } catch { }

            // Do NOT: wait on listeningTask here (can deadlock on UI/log invoke)
            // Do NOT: Close/Dispose currentSslStream here (ConnectToZNCAsync finally owns cleanup)

            lock (fishLock)
            {
                fishDecryptors.Clear();
            }

            LogManager.LogIRC(IRCEventType.Disconnection, $"Disconnect requested for {siteName}");
        }
        catch (Exception ex)
        {
            try { LogManager.LogIRC(IRCEventType.Error, $"Error during disconnect request: {ex.Message}"); } catch { }
        }
    }



    private void LoadChannelKeys(SiteConfig config)
    {
        if (!useBlowfish)
        {
            lock (fishLock)
            {
                fishDecryptors.Clear();
            }
            AppendOutput("[INFO] Blowfish/FiSH disabled; channel messages use plaintext.", Color.Cyan);
            return;
        }

        if (config?.SiteSettings == null)
        {
            AppendOutput("[WARN] Site configuration or settings are null, cannot load channel keys.", Color.Orange);
            return;
        }

        var siteSettings = config.SiteSettings;

        // Reloadable at connect time — take fishLock so a not-yet-stopped listener from
        // a previous connection can't read the dictionary mid-rebuild.
        lock (fishLock)
        {
            // Rebuild from scratch so stale channels don't linger across a reconnect.
            fishDecryptors.Clear();

            // 1) Load chan1..chan20 / blowfish_key1..20
            for (int i = 1; i <= 20; i++)
            {
                var chanProp = siteSettings.GetType().GetProperty($"Chan{i}");
                var keyProp = siteSettings.GetType().GetProperty($"BlowfishKey{i}");

                if (chanProp == null || keyProp == null)
                    continue;

                var chanValue = (chanProp.GetValue(siteSettings) as string)?.Trim();
                var encKey = keyProp.GetValue(siteSettings) as string;

                if (string.IsNullOrWhiteSpace(chanValue) || string.IsNullOrWhiteSpace(encKey))
                    continue;

                // Normalize the channel name the SAME way SetChannelKey (chatbox) does,
                // so JSON/editor keys match the server's channel name regardless of a
                // missing '#' or different casing.
                if (!chanValue.StartsWith("#") && !chanValue.StartsWith("PM:"))
                    chanValue = "#" + chanValue.TrimStart('#');

                try
                {
                    var plainKey = SecureConfig.Decrypt(encKey);

                    if (!string.IsNullOrWhiteSpace(plainKey))
                    {
                        // overwrite if already present, that's fine
                        fishDecryptors[chanValue] = new FishDecryptor(plainKey);
                    }
                }
                catch (Exception ex)
                {
                    AppendOutput($"[ERROR] Failed to load Blowfish key for {chanValue}: {ex.Message}", Color.Red);
                }
            }

            // 2) Load chat-only keys from site_settings.chat_keys
            if (siteSettings.ChatKeys != null)
            {
                foreach (var kvp in siteSettings.ChatKeys)
                {
                    var channel = kvp.Key;
                    var encKey = kvp.Value;

                    if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(encKey))
                        continue;

                    try
                    {
                        var plainKey = SecureConfig.Decrypt(encKey);

                        if (!string.IsNullOrWhiteSpace(plainKey))
                        {
                            fishDecryptors[channel] = new FishDecryptor(plainKey);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppendOutput($"[ERROR] Failed to load chat key for {channel}: {ex.Message}", Color.Red);
                    }
                }
            }
        }

        // 3) Final log
        List<string> channelKeys;
        lock (fishLock)
        {
            channelKeys = fishDecryptors.Keys
                .Where(k => k.StartsWith("#"))
                .ToList();
        }

        if (channelKeys.Any())
        {
            AppendOutput(
                $"[INFO] Blowfish decryption initialized for {channelKeys.Count} channel(s): {string.Join(", ", channelKeys)}",
                Color.Green);
        }
        else
        {
            AppendOutput("[WARN] No Blowfish keys configured for any channels. Messages will not be decrypted.", Color.Orange);
        }
    }

    public void SetChannelKey(string channel, string utf8Key, bool persist)
    {
        if (!useBlowfish)
            return;

        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(utf8Key))
            return;

        channel = channel.Trim();

        if (!channel.StartsWith("#") && !channel.StartsWith("PM:"))
            channel = "#" + channel.TrimStart('#');

        try
        {
            // Update runtime FiSH decryptor
            lock (fishLock)
            {
                fishDecryptors[channel] = new FishDecryptor(utf8Key);
            }

            if (!persist)
                return;

            // Ensure dictionary exists
            if (siteConfig.SiteSettings.ChatKeys == null)
                siteConfig.SiteSettings.ChatKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var encKey = SecureConfig.Encrypt(utf8Key);
            siteConfig.SiteSettings.ChatKeys[channel] = encKey;

            // Persist to JSON file
            var siteFile = Path.Combine("sites", $"{siteConfig.SiteSettings.Sitename}.json");
            if (!File.Exists(siteFile))
            {
                AppendOutput($"[WARN] Site file not found when saving key: {siteFile}", Color.Yellow);
                return;
            }

            var json = File.ReadAllText(siteFile);
            var fileConfig = JsonConvert.DeserializeObject<SiteConfig>(json) ?? new SiteConfig();
            if (fileConfig.SiteSettings == null)
                fileConfig.SiteSettings = new SiteSettings();
            if (fileConfig.SiteSettings.ChatKeys == null)
                fileConfig.SiteSettings.ChatKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            fileConfig.SiteSettings.ChatKeys[channel] = encKey;

            AtomicFile.WriteAllText(siteFile, JsonConvert.SerializeObject(fileConfig, Formatting.Indented));

            AppendOutput($"[FiSH] Saved key for {channel} to site config", Color.Green);
        }
        catch (Exception ex)
        {
            AppendOutput($"[ERROR] Failed to save channel key for {channel}: {ex.Message}", Color.Red);
        }
    }

    public void SetChannelBlowfishKey(string channel, string utf8Key)
    {
        if (!useBlowfish)
            return;

        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(utf8Key))
            return;

        try
        {
            lock (fishLock)
            {
                fishDecryptors[channel] = new FishDecryptor(utf8Key);
            }

            AppendOutput($"[FiSH] Manual Blowfish key set for channel {channel}", Color.Yellow);
        }
        catch (Exception ex)
        {
            AppendOutput($"[ERROR] Failed to set Blowfish key for {channel}: {ex.Message}", Color.Red);
        }
    }

    public async Task ConnectToZNCAsync()
    {
        Interlocked.Exchange(ref _disconnecting, 0);

        // Disconnect() clears fishDecryptors, and the ctor is the only other place that
        // loaded them — so a reconnect on the same instance would leave every channel
        // without a key and silently ignore all encrypted announces. Reload here.
        LoadChannelKeys(siteConfig);

        TcpClient tcpClient = null;
        SslStream sslStream = null;

        try
        {
            localCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port);
            this.currentTcpClient = tcpClient;

            AppendOutput($"[INFO] Connected to IRC server: {host}:{port}", Color.Green);

            sslStream = new SslStream(
                tcpClient.GetStream(),
                false,
                (sender, certificate, chain, sslPolicyErrors) => true
            );

            this.currentSslStream = sslStream;

            await sslStream.AuthenticateAsClientAsync(host);
            AppendOutput("[INFO] SSL/TLS authentication successful.", Color.Green);

            if (!string.IsNullOrWhiteSpace(zncPass))
            {
                await SendMessageAsync(sslStream, $"PASS {zncPass}");
            }

            await SendMessageAsync(sslStream, $"NICK {zncLoginUser}");
            await SendMessageAsync(sslStream, $"USER {zncLoginUser} 0 * :{zncLoginUser}");

            var isRegistered = await WaitForRegistrationOrTimeoutAsync(sslStream, tcpClient, TimeSpan.FromSeconds(8));

            if (!isRegistered)
            {
                AppendOutput("[WARN] Registration not confirmed; joining channels anyway.", Color.Yellow);
            }

            foreach (var channel in channels)
            {
                if (string.IsNullOrWhiteSpace(channel))
                    continue;

                await SendMessageAsync(sslStream, $"JOIN {channel}");
                AppendOutput($"[INFO] Sent JOIN for channel: {channel}", Color.Cyan);
            }

            listeningTask = ListenForMessagesAsync(sslStream);
            await listeningTask;
        }
        catch (OperationCanceledException)
        {
            AppendOutput($"[WARN] Connection canceled for Site {siteName}.", Color.Orange);
        }
        catch (Exception ex)
        {
            AppendOutput($"[ERROR] Error connecting to IRC server for Site {siteName}: {ex.Message}", Color.Red);
        }
        finally
        {
            // Ensure we stop the listener first
            try { localCancellationTokenSource?.Cancel(); } catch { }

            // Close in safe order: socket -> stream
            var tcp = Interlocked.Exchange(ref currentTcpClient, null);
            try { tcp?.Close(); } catch { }

            var stream = Interlocked.Exchange(ref currentSslStream, null);
            try { stream?.Close(); } catch { }
            try { stream?.Dispose(); } catch { }

            var cts = Interlocked.Exchange(ref localCancellationTokenSource, null);
            try { cts?.Dispose(); } catch { }

            listeningTask = null;
        }
    }


    // Announce-path regex cache. Patterns come from the site config and change
    // only on config edits, so compiling them once and reusing removes per-line
    // pattern parsing from the critical path.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> AnnounceRegexCache =
        new System.Collections.Concurrent.ConcurrentDictionary<string, Regex>();

    private static Regex GetCachedRegex(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
            return null;

        return AnnounceRegexCache.GetOrAdd(pattern, p =>
        {
            try
            {
                return new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled);
            }
            catch
            {
                // Invalid user pattern: fall back to interpreted so a bad regex
                // surfaces as a normal match failure instead of crashing.
                return new Regex(Regex.Escape(p), RegexOptions.IgnoreCase);
            }
        });
    }

    // Returns the ZNC network to attach to: explicit configured network first,
    // else the "/network" suffix on the username. "" when neither is present.
    private static string ResolveNetwork(string configuredNetwork, string configuredUsername)
    {
        if (!string.IsNullOrWhiteSpace(configuredNetwork))
            return configuredNetwork.Trim();

        var user = (configuredUsername ?? string.Empty).Trim();
        var slash = user.IndexOf('/');
        if (slash >= 0 && slash < user.Length - 1)
            return user.Substring(slash + 1).Trim();

        return string.Empty;
    }

    // Strips any "/network" or "@identifier" suffix, leaving the bare account.
    private static string AccountName(string configuredUsername)
    {
        var user = (configuredUsername ?? string.Empty).Trim();
        var slash = user.IndexOf('/');
        if (slash >= 0) user = user.Substring(0, slash);
        var at = user.IndexOf('@');
        if (at >= 0) user = user.Substring(0, at);
        return user.Trim();
    }

    // Builds the ZNC PASS payload "account/network:password" used to both
    // authenticate and select the network.
    private static string BuildZncPassword(string configuredUsername, string network, string decryptedPassword)
    {
        if (string.IsNullOrEmpty(decryptedPassword))
            return decryptedPassword;

        var account = AccountName(configuredUsername);
        if (string.IsNullOrWhiteSpace(account))
            return decryptedPassword;

        var login = string.IsNullOrWhiteSpace(network) ? account : $"{account}/{network}";
        if (decryptedPassword.StartsWith(login + ":", StringComparison.OrdinalIgnoreCase))
            return decryptedPassword;

        return $"{login}:{decryptedPassword}";
    }

    private async Task<bool> WaitForRegistrationOrTimeoutAsync(
        SslStream sslStream,
        TcpClient tcpClient,
        TimeSpan timeout)
    {
        var buffer = new byte[4096];
        var start = DateTime.UtcNow;

        while (!cancellationToken.IsCancellationRequested &&
               DateTime.UtcNow - start < timeout)
        {
            if (!sslStream.CanRead || !tcpClient.Connected)
                break;

            if (tcpClient.Available == 0)
            {
                await Task.Delay(100, cancellationToken);
                continue;
            }

            int bytesRead;
            try
            {
                bytesRead = await sslStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                AppendOutput($"[WARN] Error while waiting for registration: {ex.Message}", Color.Orange);
                return false;
            }

            if (bytesRead <= 0)
                return false;

            string text = System.Text.Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r', '\n');
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                AppendOutput($"[RECV-REG] {line}", Color.DimGray);

                if (line.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = line.Substring(4).TrimStart(':', ' ');
                    await SendMessageAsync(sslStream, $"PONG :{payload}");
                }

                if (line.Contains(" 001 ") || line.Contains(" 376 ") || line.Contains(" 422 "))
                {
                    AppendOutput("[INFO] Registration complete, proceeding to JOIN.", Color.Green);
                    return true;
                }
            }
        }

        AppendOutput("[WARN] No registration numeric (001/376/422) seen, joining channels anyway.", Color.Yellow);
        return false;
    }


    private async Task ListenForMessagesAsync(SslStream sslStream)
    {
        try
        {
            byte[] buffer = new byte[4096];
            var messageBuilder = new StringBuilder();

            while (!localCancellationTokenSource.Token.IsCancellationRequested)
            {
                int bytesRead = await sslStream.ReadAsync(buffer, 0, buffer.Length, localCancellationTokenSource.Token);
                if (bytesRead == 0) break;

                string data = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                messageBuilder.Append(data);

                string messages = messageBuilder.ToString();
                string[] lines = messages.Split(new[] { "\r\n" }, StringSplitOptions.None);

                for (int i = 0; i < lines.Length - 1; i++)
                {
                    string line = lines[i];

                    if (line.StartsWith("PING"))
                    {
                        string pongResponse = "PONG" + line.Substring(4); // only rewrite the command, not e.g. a token containing "PING"
                        await SendMessageAsync(sslStream, pongResponse);
                        continue;
                    }

                    if (IsAnnounceFromBot(line))
                    {
                        // Dispatch WITHOUT awaiting: release/IMDB/TVMaze/pretime lookups and
                        // the cbftp spreadjob can take seconds, and awaiting them here would
                        // stall the read loop — PINGs go unanswered (server ping-timeout) and
                        // later announces queue up. Writes are serialized by _sendGate, and
                        // ProcessBotMessageAsync wraps its whole body in try/catch, so this is
                        // safe. Capture the line in a local for the closure.
                        string lineCopy = line;
                        _ = Task.Run(() => ProcessBotMessageAsync(lineCopy, sslStream));
                    }
                }

                messageBuilder.Clear();
                messageBuilder.Append(lines[lines.Length - 1]);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown - don't log
        }
        catch (ObjectDisposedException)
        {
            // Stream closed during shutdown - don't log
        }
        catch (IOException) when (IsDisconnecting)
        {
            // IO error during disconnect - expected, don't log
        }
        catch (IOException ex)
        {
            if (!IsDisconnecting)
            {
                AppendOutput($"[WARN] Connection lost for {siteName}: {ex.Message}", Color.Orange);
            }
        }
        catch (Exception ex)
        {
            if (!IsDisconnecting)
            {
                AppendOutput($"[ERROR] Listening error for {siteName}: {ex.Message}", Color.Red);
            }
        }
    }

    /// <summary>
    /// Is this line an announce from the site's configured bot?
    ///
    /// This single test is what the whole racer hangs on: say no and nothing is parsed,
    /// no race starts, and neither the Race nor the CBFTP log ever sees the release.
    /// It therefore has to stay a SUPERSET of the original substring check - never
    /// stricter. The old behaviour is tried first and is on its own enough; the extra
    /// case below only ADDS lines that the substring test cannot see.
    /// </summary>
    private bool IsAnnounceFromBot(string line)
    {
        if (string.IsNullOrEmpty(botName))
            return false;

        // Original behaviour, unchanged. Anything that worked before still works.
        if (line.Contains($":{botName}!"))
            return true;

        // Only extra: a bouncer that omits the user@host part of the prefix
        // (":bot PRIVMSG #chan :...") or prefixes IRCv3 message tags
        // ("@time=... :bot!u@h PRIVMSG ..."), which ZNC does once server-time is on.
        var probe = line;
        if (probe.Length > 0 && probe[0] == '@')
        {
            var tagEnd = probe.IndexOf(' ');
            if (tagEnd < 0) return false;
            probe = probe.Substring(tagEnd + 1);
        }

        return probe.StartsWith($":{botName} ", StringComparison.OrdinalIgnoreCase) ||
               probe.StartsWith($":{botName}!", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ProcessBotMessageAsync(string line, SslStream sslStream)
    {
        try
        {
            if (EngineSettings.DebugEnabled)
            {
                AppendOutput($"[DEBUG] Received IRC line: {line}", Color.Cyan);
            }

            var channelMatch = Regex.Match(line, @" PRIVMSG (#\S+)");
            if (!channelMatch.Success)
            {
                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput($"[DEBUG] No channel match found in line: {line}", Color.Orange);
                }
                return;
            }

            string channelName = channelMatch.Groups[1].Value;
            if (!channels.Contains(channelName, StringComparer.OrdinalIgnoreCase))
            {
                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput($"[DEBUG] Channel '{channelName}' is not being monitored. Ignoring message.", Color.Orange);
                }
                return;
            }

            // Encrypted or plain text handling.
            // FiSH encrypted payloads are prefixed with either "+OK " or "mcps "
            // (Mircryption). Some networks/bots only ever use "mcps ", so we must
            // detect both - FishDecryptor.DecryptMessage already strips either one.
            string decryptedMessage;

            // Look for the marker at the start of the trailing parameter (" :+OK ...")
            // rather than anywhere in the raw line — otherwise a plaintext announce
            // that merely CONTAINS "+OK " is fed to the decryptor and dropped on failure.
            int startIdx = line.IndexOf(" :+OK ", StringComparison.Ordinal);
            if (startIdx == -1)
                startIdx = line.IndexOf(" :mcps ", StringComparison.Ordinal);
            if (startIdx != -1)
                startIdx += 2; // skip the " :" separator

            if (startIdx != -1)
            {
                // ENCRYPTED MESSAGE
                string encryptedMessage = line.Substring(startIdx).Trim();
                FishDecryptor fishDecryptor;
                lock (fishLock)
                {
                    if (!fishDecryptors.TryGetValue(channelName, out fishDecryptor))
                    {
                        if (EngineSettings.DebugEnabled)
                        {
                            AppendOutput($"[DEBUG] No Blowfish decryptor found for channel: {channelName}", Color.Orange);
                        }
                        return;
                    }
                }
                if (IsDisconnecting)
                    return;

                try
                {
                    decryptedMessage = fishDecryptor.DecryptMessage(encryptedMessage);
                }
                catch (Exception ex)
                {
                    var message = $"Failed to decrypt FiSH message for site '{siteName}' on {channelName}: {ex.Message}. Check the channel key; paste it without the cbc: prefix.";
                    AppendOutput($"[ERROR] {message}", Color.Red);
                    LogManager.LogIRC(IRCEventType.Error, message);
                    return;
                }

                if (string.IsNullOrEmpty(decryptedMessage))
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        AppendOutput($"[DEBUG] Failed to decrypt message: {encryptedMessage}", Color.Red);
                    }
                    return;
                }

                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput($"[DEBUG] Decrypted message: {decryptedMessage}", Color.Cyan);
                }
            }
            else
            {
                // PLAIN TEXT MESSAGE
                var msgMatch = Regex.Match(line, @":[^ ]+ PRIVMSG [^ ]+ :(.+)$");
                if (!msgMatch.Success)
                {
                    if (EngineSettings.DebugEnabled)
                    {
                        AppendOutput($"[DEBUG] No encrypted or plain-text payload found in line: {line}", Color.Orange);
                    }
                    return;
                }

                decryptedMessage = msgMatch.Groups[1].Value;

                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput($"[DEBUG] Using plain-text payload: {decryptedMessage}", Color.Cyan);
                }
            }

            string cleanMessage = StripIrcColors(decryptedMessage).Trim();
            if (EngineSettings.DebugEnabled)
            {
                AppendOutput($"[DEBUG] Cleaned message: {cleanMessage}", Color.Cyan);
            }

            if (ShouldSkipMessage(cleanMessage))
            {
                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput($"[DEBUG] Message skipped due to ignored words: {cleanMessage}", Color.Orange);
                }
                return;
            }

            // Detect NEW vs PRE
            bool isPreLine = false;
            bool isNewLine = false;

            if (preFieldRegex != null && preFieldRegex.IsMatch(cleanMessage))
            {
                isPreLine = true;
            }
            else if (newFieldRegex != null && newFieldRegex.IsMatch(cleanMessage))
            {
                isNewLine = true;
            }

            if ((preFieldRegex != null || newFieldRegex != null) && !isPreLine && !isNewLine)
            {
                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput($"[DEBUG] Message does not match NEW or PRE regex, skipping: {cleanMessage}", Color.Orange);
                }
                return;
            }

            if (EngineSettings.DebugEnabled)
            {
                var typeStr = isPreLine ? "PRE" : (isNewLine ? "NEW" : "UNKNOWN");
                AppendOutput($"[DEBUG] Line classified as: {typeStr}", Color.Cyan);
            }

            // Global PreBot → resolve linked site
            var isGlobalPreBotMode = IsConfiguredGlobalPreBotMode(PreOrSite);
            SiteConfig linkedSiteConfig = siteConfig;
            if (isGlobalPreBotMode)
            {
                var linkedSiteName = siteConfig.SiteSettings.Sitename;
                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput($"[INFO] Using Global PreBot '{PreOrSite}' for capturing releases. Linked to site: '{linkedSiteName}'.", Color.Green);
                }

                // Use the shared, cached config instead of re-reading and
                // re-deserializing the site JSON from disk on every announce.
                // The cache is invalidated whenever a site is saved.
                if (!SiteConfigManager.TryGetSiteConfig(linkedSiteName, out linkedSiteConfig) || linkedSiteConfig == null)
                {
                    AppendOutput($"[ERROR] Failed to load linked site configuration for '{linkedSiteName}'. Skipping processing.", Color.Red);
                    return;
                }
            }

            // Choose regexes depending on NEW vs PRE
            string sectionPattern;
            string releasePattern;

            if (isPreLine)
            {
                sectionPattern = !string.IsNullOrEmpty(siteConfig.SiteSettings.PreSectionRegexPattern)
                    ? siteConfig.SiteSettings.PreSectionRegexPattern
                    : siteConfig.SiteSettings.SectionRegexPattern;

                releasePattern = !string.IsNullOrEmpty(siteConfig.SiteSettings.PreReleaseRegexPattern)
                    ? siteConfig.SiteSettings.PreReleaseRegexPattern
                    : siteConfig.SiteSettings.ReleaseRegexPattern;
            }
            else
            {
                sectionPattern = siteConfig.SiteSettings.SectionRegexPattern;
                releasePattern = siteConfig.SiteSettings.ReleaseRegexPattern;
            }

            // Compiled-and-cached: building a Regex per announce re-parses the
            // pattern on the hot path for every single line. These patterns change
            // only when the site config changes, so cache them by pattern text.
            var effectiveSectionRegex = GetCachedRegex(sectionPattern);
            var effectiveReleaseRegex = GetCachedRegex(releasePattern);

            if (EngineSettings.DebugEnabled)
            {
                AppendOutput($"[DEBUG] Using SectionRegexPattern: {sectionPattern}", Color.Cyan);
                AppendOutput($"[DEBUG] Using ReleaseRegexPattern: {releasePattern}", Color.Cyan);
            }

            if (effectiveSectionRegex == null || effectiveReleaseRegex == null)
            {
                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput("[WARN] Section or release regex not configured; skipping line.", Color.Yellow);
                }
                return;
            }

            var releaseMatch = effectiveReleaseRegex.Match(cleanMessage);
            var sectionMatch = effectiveSectionRegex.Match(cleanMessage);

            if (!releaseMatch.Success || !sectionMatch.Success)
            {
                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput($"[WARN] Failed to extract release or section from: {cleanMessage}", Color.Yellow);
                }
                return;
            }

            string releaseName = releaseMatch.Groups[1].Value.Trim();
            string section = sectionMatch.Groups[1].Value.Trim();

            // If PRE line, trim with PRE prefix/suffix
            if (isPreLine)
            {
                var prePrefix = siteConfig.SiteSettings.PreSectionPrefix ?? string.Empty;
                var preSuffix = siteConfig.SiteSettings.PreSectionSuffix ?? string.Empty;

                if (!string.IsNullOrEmpty(prePrefix) && section.StartsWith(prePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    section = section.Substring(prePrefix.Length);
                }

                if (!string.IsNullOrEmpty(preSuffix) && section.EndsWith(preSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    section = section.Substring(0, section.Length - preSuffix.Length);
                }

                if (EngineSettings.DebugEnabled)
                {
                    AppendOutput($"[DEBUG] PRE line → normalized section: {section}", Color.Cyan);
                }
            }

            AppendOutput($"[{siteName}] [{botName}] [{section}] {releaseName}", Color.LightBlue);

            // Store pretime immediately (BEFORE ANY CHECKS!), but only for real PRE
            // lines or raw PreBot feeds. A normal NEW/race announce must not seed
            // the pretime cache, otherwise max-pretime checks always see 0s/1s.
            var isPreBotAnnounceMode =
                isGlobalPreBotMode ||
                string.Equals(PreOrSite, "PreBot", StringComparison.OrdinalIgnoreCase);

            var shouldStorePretime =
                isPreLine ||
                (isPreBotAnnounceMode && !isNewLine);

            if (shouldStorePretime)
            {
                // The pretime write stays awaited: the value is used by the pretime
                // rules further down. The read-back exists only to log whether we
                // were the first PreBot, so it runs off the critical path — it must
                // never delay the cbftp command.
                await PreBotManager.StorePretimeAsync(releaseName, section);

                var announceSiteName = siteName;
                var announceRelease = releaseName;
                // Stamp the announce time HERE, not inside the background task, or
                // the "first PreBot" measurement would include thread-pool delay.
                var announceAt = DateTime.UtcNow;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        var storedPretime = await SQLiteHelper.GetPretimeAsync(announceRelease);
                        if (storedPretime.HasValue)
                        {
                            var diff = (announceAt - storedPretime.Value).TotalMilliseconds;
                            if (diff < 100)
                            {
                                LogManager.Success($"[{announceSiteName}] FIRST PreBot to announce [{announceRelease}]");
                            }
                            else
                            {
                                LogManager.Debug($"[{announceSiteName}] PreBot announced [{announceRelease}] {diff:F0}ms after first PreBot");
                            }
                        }
                    }
                    catch { }
                });
            }

            LogManager.LogRace(RaceStatus.Detected, releaseName, siteName, quality: section, ircChannel: channelName);

            // Check: Already processed?
            if (await SQLiteHelper.IsReleaseProcessedAsync(releaseName))
            {
                // Name the channel and quote the line. "Already processed" on its own is
                // unactionable: it says a repeat arrived but not where from, so a second
                // announce, a second monitored channel carrying the same feed and an
                // actual bug all look identical.
                LogManager.LogRace(RaceStatus.Filtered, releaseName, siteName,
                    filterReason: "Already processed", ircChannel: channelName);

                // Quoting the line is what matters: it is how a mis-tuned announce regex
                // (a "NEW LEADER:" race-progress line slipping through a \bNEW\b filter,
                // say) is told apart from a genuine repeat.
                LogManager.LogIRC(IRCEventType.Announce,
                    $"Repeat announce for '{releaseName}': {cleanMessage}",
                    channel: channelName, server: siteName);

                RaceDiagnostics.Report(releaseName, SkipReason.Duplicate,
                    $"repeat announce on {channelName}: {cleanMessage}", null, section, siteName);
                return;
            }
                      
            // Check: Section allowed? (Skip for Global PreBots)
            if (!isGlobalPreBotMode)
            {
                // Not a Global PreBot here, so linkedSiteConfig == siteConfig and the
                // JObject built once in the constructor applies. Re-serializing the
                // whole config graph per announce was pure hot-path overhead.
                if (!RaceHelper.IsAllowedSection(section, siteConfigJson))
                {
                    LogManager.LogRace(RaceStatus.Filtered, releaseName, siteName, filterReason: $"Section '{section}' disabled", ircChannel: channelName);
                    RaceDiagnostics.Report(releaseName, SkipReason.SectionDisabled,
                        $"section '{section}' is not enabled on the announcing site", siteName, section, siteName);
                    return;
                }
            }

            // Decide which prefix/suffix to use for mapping
            var mapSectionPrefix = isPreLine
                ? (linkedSiteConfig.SiteSettings.PreSectionPrefix ?? linkedSiteConfig.SiteSettings.SectionPrefix)
                : linkedSiteConfig.SiteSettings.SectionPrefix;

            var mapSectionSuffix = isPreLine
                ? (linkedSiteConfig.SiteSettings.PreSectionSuffix ?? linkedSiteConfig.SiteSettings.SectionSuffix)
                : linkedSiteConfig.SiteSettings.SectionSuffix;

            // Map CBFTP section
            string cbftpSection = RaceHelper.GetMappedCbftpSection(
                section,
                releaseName,
                siteConfigJson,
                mapSectionPrefix,
                mapSectionSuffix);

            if (string.IsNullOrEmpty(cbftpSection) || cbftpSection.StartsWith("[ERROR]"))
            {
                LogManager.Warning($"[{siteName}] IRC section [{section}] is not configured in any site.");
                LogManager.Info($"[{siteName}] To race this section: Add [{section}] to a site's IRC sections and map it with a CBFTP mapping and enable it in Race Sections.");
                RaceDiagnostics.Report(releaseName, SkipReason.NoSectionMapping,
                    $"IRC section '{section}' has no cbftp mapping on any site", null, section, siteName);
                return;
            }

            LogManager.LogCBFTP(CBFTPEventType.Info,
                $"[{LogColors.Magenta(siteName)}] Mapped [{LogColors.Green(section)}] → CBFTP: [{LogColors.Green(cbftpSection)}] for release: [{LogColors.Orange(releaseName)}]");

            // Filter allowed sites (THIS IS WHERE PRETIME/IMDB/TVMAZE CHECKS HAPPEN NOW)
            var raceSectionsDictionary = linkedSiteConfig.RaceSectionsEnabled
                ?.ToDictionary(s => s, s => string.Empty) ?? new Dictionary<string, string>();

            var filterResult = await RaceHelper.FilterAllowedSites(
                raceSectionsDictionary,
                mappings,
                blacklist,
                cbftpSection,
                releaseName,
                cleanMessage,
                mapSectionPrefix,
                mapSectionSuffix,
                linkedSiteConfig.SiteSettings.Sitename,
                isGlobalPreBotMode ? section : null);

            switch (filterResult.Status)
            {
                case FilterStatus.Duplicate:
                case FilterStatus.NoSites:
                case FilterStatus.InsufficientSites:
                    LogManager.LogRace(RaceStatus.Filtered, releaseName, siteName, filterReason: filterResult.Message, ircChannel: channelName);
                    return;

                case FilterStatus.Error:
                    LogManager.LogRace(RaceStatus.Failed, releaseName, siteName, filterReason: filterResult.Message, ircChannel: channelName);
                    return;

                case FilterStatus.Success:
                    break;

                default:
                    LogManager.Error($"[{siteName}] Unknown filter status: {filterResult.Status} for {releaseName}");
                    return;
            }

            // Start transfer
            var targetSites = string.Join(",", filterResult.AllowedSites);
            LogManager.LogRace(RaceStatus.Racing, releaseName, siteName, targetSite: targetSites, quality: section, ircChannel: channelName);
            await CbftpRacer.HandleTransferJob(cbftpSection, releaseName, filterResult, siteName, channelName);
        }
        catch (Exception ex)
        {
            var errorMessage = $"[ERROR] Exception: {ex.Message}";
            AppendOutput(errorMessage, Color.Red);
            LogManager.Error($"[{siteName}] Processing failed: {ex.Message}");
        }
    }

    private SiteConfig LoadConfiguration(string filePath)
    {
        if (!File.Exists(filePath))
        {
            AppendOutput($"[ERROR] Configuration file '{filePath}' does not exist.", Color.Red);
            return null;
        }

        try
        {
            var jsonContent = File.ReadAllText(filePath);
            var siteConfig = JsonConvert.DeserializeObject<SiteConfig>(jsonContent);

            if (siteConfig == null)
            {
                AppendOutput($"[ERROR] Failed to deserialize configuration file '{filePath}'.", Color.Red);
            }

            return siteConfig;
        }
        catch (Exception ex)
        {
            AppendOutput($"[ERROR] Exception while loading configuration file '{filePath}': {ex.Message}", Color.Red);
            return null;
        }
    }

    private bool ShouldSkipMessage(string message)
    {
        if (ignoreWords == null || !ignoreWords.Any())
        {
            return false;
        }

        var normalizedMessage = message.ToLower()
            .Replace("/", " ")
            .Replace(".", " ")
            .Replace("-", " ")
            .Replace("_", " ");

        var messageWords = Regex.Matches(normalizedMessage, @"\w+")
                                .Cast<Match>()
                                .Select(m => m.Value)
                                .ToList();

        var matchedWord = messageWords.FirstOrDefault(word =>
            ignoreWords.Contains(word, StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(matchedWord))
        {
            if (EngineSettings.DebugEnabled)
            {
                AppendOutput($"[SKIP] Release skipped due to ignored word '{matchedWord}' in: {message}", Color.Orange);
            }
            return true;
        }

        return false;
    }

    private async Task SendMessageAsync(SslStream sslStream, string message)
    {
        if (IsDisconnecting)
            return;

        if (sslStream == null)
            return;

        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (IsDisconnecting)
                return;

            if (!sslStream.CanWrite)
                return;

            byte[] buffer = Encoding.UTF8.GetBytes(message + "\r\n");
            await sslStream.WriteAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
            await sslStream.FlushAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // ignore: shutdown/disconnect
        }
        catch (IOException)
        {
            // ignore: socket aborted during shutdown
        }
        catch (Exception ex)
        {
            if (!IsDisconnecting)
                AppendOutput($"Error sending message for Site {siteName}: {ex.Message}", Color.Red);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private static bool IsConfiguredGlobalPreBotMode(string mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return false;

        var match = Regex.Match(mode, @"^Global\s+PreBot\s*\(([^)]+)\)\s*$", RegexOptions.IgnoreCase);
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups[1].Value);
    }

    private string StripIrcColors(string text)
    {
        // \x1D italic and \x1E strikethrough included (they corrupted release-name parsing); \x14 is not an mIRC code
        return Regex.Replace(text, @"(\x03\d{0,2}(,\d{0,2})?|[\x02\x0F\x16\x1D\x1E\x1F])", "");
    }
}
