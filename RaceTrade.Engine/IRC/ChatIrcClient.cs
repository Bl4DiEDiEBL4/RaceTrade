using RaceTrade.Engine.Logging;
using RaceTrade.Engine.Compat;
using Newtonsoft.Json;
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

public class ChatIrcClient
{
    private readonly string host;
    private readonly int port;
    private readonly string username;
    private readonly string expectedNetwork; // ZNC network this site must attach to ("" if unknown)
    private readonly string ircNick;
    private readonly string zncPassword;
    private readonly string botName;
    private readonly string siteName;
    private List<string> channels;
    private readonly Action<string, string> callback;
    private readonly IIrcOutput logOutput;
    private readonly SiteConfig siteConfig;
    private readonly CancellationToken cancellationToken;

    // Blowfish for channels / PM.
    // Channel names and IRC nicks are case-insensitive, so these must be too.
    // ALL access (read and write) must be inside lock (fishLock).
    private readonly Dictionary<string, FishDecryptor> fishDecryptors = new Dictionary<string, FishDecryptor>(StringComparer.OrdinalIgnoreCase);

    // FiSH PM key handling
    private readonly Dictionary<string, string> pmFishKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DH1080> pendingKeyExchanges = new Dictionary<string, DH1080>(StringComparer.OrdinalIgnoreCase);

    // Tabbed IRC UI
    private IChannelOutput tabbedLogOutput;

    // User tracking
    private readonly Dictionary<string, HashSet<string>> channelUsers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> pendingNamesLists = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> connectedStatusChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool enableUserTracking = false;
    private readonly object userTrackingLock = new object();

    private SslStream currentSslStream;
    private bool chatOnlyMode = false;

    private readonly Dictionary<string, Dictionary<string, char>> channelUserPrefixes =
    new Dictionary<string, Dictionary<string, char>>(StringComparer.OrdinalIgnoreCase);

    private readonly object fishLock = new object();
    private CancellationTokenSource localCancellationTokenSource;
    private Task listeningTask;
    private TcpClient currentTcpClient;
    private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);
    private int _disconnecting = 0;
    private bool IsDisconnecting => Volatile.Read(ref _disconnecting) == 1;

    // NOTE: there used to be a separate _keyLock here. Writers took _keyLock (or no
    // lock at all) while readers took fishLock, so the two never excluded each other
    // and the dictionaries could be mutated mid-read. Everything now uses fishLock.

    public ChatIrcClient(SiteConfig config, string siteName, Action<string, string> callback, IIrcOutput logOutput, CancellationToken cancellationToken)
    {
        if (config == null)
            throw new ArgumentNullException(nameof(config), "Site configuration cannot be null.");

        this.siteConfig = config;
        this.siteName = siteName;
        this.callback = callback;
        this.logOutput = logOutput;
        this.cancellationToken = cancellationToken;

        // Server settings
        if (string.IsNullOrEmpty(config.Server?.Host))
            throw new ArgumentException("Host cannot be null or empty.");

        this.host = config.Server.Host;
        this.port = config.Server.Port > 0 ? config.Server.Port : 6667;

        if (string.IsNullOrEmpty(config.Server.Username))
            throw new ArgumentException("Username cannot be null or empty.");

        this.username = config.Server.Username.Trim();

        // Resolve the ZNC network: explicit Server.Network wins; otherwise fall
        // back to a "user/network" style Username. This is what guarantees each
        // site's JOINs land on the correct network instead of ZNC's default.
        this.expectedNetwork = ResolveNetwork(config.Server.Network, this.username);
        this.ircNick = BuildIrcNick(this.username);

        if (string.IsNullOrEmpty(config.Server.Password))
            throw new ArgumentException("Password cannot be null or empty.");

        var decryptedPassword = SecureConfig.Decrypt(config.Server.Password);
        this.zncPassword = BuildZncPassword(this.username, this.expectedNetwork, decryptedPassword);

        if (string.IsNullOrEmpty(config.SiteSettings?.BotName))
            throw new ArgumentException("Bot name cannot be null or empty.");

        this.botName = config.SiteSettings.BotName;

        // Join ALL configured channels (Chan1..Chan20), not just the first three.
        // The site editor supports 20 and LoadChannelKeys loads keys for all 20, so
        // anything past Chan3 was never joined and never decrypted.
        this.channels = new List<string>();
        for (int i = 1; i <= 20; i++)
        {
            var chanProp = config.SiteSettings.GetType().GetProperty($"Chan{i}");
            var chanValue = chanProp?.GetValue(config.SiteSettings) as string;

            if (string.IsNullOrWhiteSpace(chanValue))
                continue;

            chanValue = NormalizeKeyName(chanValue);

            if (!this.channels.Contains(chanValue, StringComparer.OrdinalIgnoreCase))
                this.channels.Add(chanValue);
        }

        LoadChannelKeys(config);
    }

    private void AppendOutput(string message, Color color)
    {
        if (logOutput != null && !logOutput.IsDisposed)
        {
            logOutput.AppendLog(message, color);
        }
    }

    private static string BuildIrcNick(string configuredUsername)
    {
        var nick = (configuredUsername ?? string.Empty).Trim();

        var slashIndex = nick.IndexOf('/');
        if (slashIndex > 0)
            nick = nick.Substring(0, slashIndex);

        var atIndex = nick.IndexOf('@');
        if (atIndex > 0)
            nick = nick.Substring(0, atIndex);

        nick = Regex.Replace(nick, @"[^A-Za-z0-9_\-\[\]\\`^{}|]", "_").Trim('_');
        return string.IsNullOrWhiteSpace(nick) ? "RaceTrade" : nick;
    }

    // ResolveNetwork returns the ZNC network name this connection must attach to.
    // Precedence: explicit configured network > the "/network" suffix on the
    // username. Returns "" when neither is available (legacy single-network setup).
    private static string ResolveNetwork(string configuredNetwork, string configuredUsername)
    {
        if (!string.IsNullOrWhiteSpace(configuredNetwork))
            return configuredNetwork.Trim();

        var user = (configuredUsername ?? string.Empty).Trim();
        var slashIndex = user.IndexOf('/');
        if (slashIndex >= 0 && slashIndex < user.Length - 1)
            return user.Substring(slashIndex + 1).Trim();

        return string.Empty;
    }

    // AccountName strips any "/network" (and "@identifier") suffix, leaving the
    // bare ZNC account name.
    private static string AccountName(string configuredUsername)
    {
        var user = (configuredUsername ?? string.Empty).Trim();
        var slash = user.IndexOf('/');
        if (slash >= 0) user = user.Substring(0, slash);
        var at = user.IndexOf('@');
        if (at >= 0) user = user.Substring(0, at);
        return user.Trim();
    }

    // BuildZncPassword builds the PASS payload ZNC uses to both authenticate and
    // select the network: "account/network:password". When no network is known,
    // keep the raw password to preserve legacy single-network setups.
    private static string BuildZncPassword(string configuredUsername, string network, string decryptedPassword)
    {
        if (string.IsNullOrEmpty(decryptedPassword))
            return decryptedPassword;

        if (string.IsNullOrWhiteSpace(network))
            return decryptedPassword;

        var account = AccountName(configuredUsername);
        if (string.IsNullOrWhiteSpace(account))
            return decryptedPassword;

        var login = $"{account}/{network}";

        // Already prefixed (user pasted "user/network:password" into the field).
        if (decryptedPassword.StartsWith(login + ":", StringComparison.OrdinalIgnoreCase))
            return decryptedPassword;

        return $"{login}:{decryptedPassword}";
    }

    /// <summary>
    /// Sets the tabbed log output for this IRC client
    /// </summary>
    public void SetTabbedLogOutput(IChannelOutput tabbedLog)
    {
        this.tabbedLogOutput = tabbedLog;
    }

    public void SetUserTrackingEnabled(bool enabled)
    {
        lock (userTrackingLock)
        {
            this.enableUserTracking = enabled;
        }

        if (EngineSettings.DebugEnabled)
        {
            AppendOutput($"[INFO] User tracking {(enabled ? "enabled" : "disabled")} for {siteName}", Color.Cyan);
        }

        // Request user list when enabling
        if (enabled && currentSslStream != null)
        {
            Task.Run(async () =>
            {
                try
                {
                    await RequestUserList();
                }
                catch (Exception ex)
                {
                    AppendOutput($"[ERROR] Failed to request user list: {ex.Message}", Color.Red);
                }
            });
        }
    }

    public void SetChatOnlyMode(bool chatOnly)
    {
        this.chatOnlyMode = chatOnly;

        if (chatOnly)
        {
            // Collect ALL configured channels (Chan1..Chan20, same as the constructor —
            // limiting to Chan1-3 here silently reintroduced the "only 3 channels" bug)
            // plus any chat_keys channels, normalized and deduped case-insensitively.
            var allChannels = new List<string>();

            for (int i = 1; i <= 20; i++)
            {
                var chanProp = siteConfig.SiteSettings.GetType().GetProperty($"Chan{i}");
                var chanValue = chanProp?.GetValue(siteConfig.SiteSettings) as string;

                if (string.IsNullOrWhiteSpace(chanValue))
                    continue;

                chanValue = NormalizeKeyName(chanValue);

                if (!allChannels.Contains(chanValue, StringComparer.OrdinalIgnoreCase))
                    allChannels.Add(chanValue);
            }

            // Add chat_keys channels
            if (siteConfig.SiteSettings.ChatKeys != null)
            {
                var chatChannels = siteConfig.SiteSettings.ChatKeys.Keys
                    .Where(k => k.StartsWith("#") && !allChannels.Contains(k, StringComparer.OrdinalIgnoreCase))
                    .ToList();
                allChannels.AddRange(chatChannels);
            }

            this.channels = allChannels;

            AppendOutput($"[INFO] Chat-only mode: monitoring {allChannels.Count} channel(s): {string.Join(", ", allChannels)}",
                Color.Green);
        }

        if (EngineSettings.DebugEnabled)
        {
            AppendOutput($"[INFO] Chat-only mode {(chatOnly ? "enabled" : "disabled")} for {siteName}", Color.Cyan);
        }
    }

    public async Task RequestUserList()
    {
        if (currentSslStream == null || !IsHandleCreated(currentSslStream))
            return;

        try
        {
            foreach (var channel in channels)
            {
                await SendMessageAsync(currentSslStream, $"NAMES {channel}");
            }
        }
        catch (Exception ex)
        {
            AppendOutput($"[ERROR] Failed to request user list: {ex.Message}", Color.Red);
        }
    }

    private bool IsHandleCreated(SslStream stream)
    {
        try
        {
            return stream != null && stream.CanWrite;
        }
        catch
        {
            return false;
        }
    }

    private void LoadChannelKeys(SiteConfig config)
    {
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

                var chanValue = chanProp.GetValue(siteSettings) as string;
                var encKey = keyProp.GetValue(siteSettings) as string;

                if (string.IsNullOrWhiteSpace(chanValue) || string.IsNullOrWhiteSpace(encKey))
                    continue;

                // Normalize like SetChannelKey does, so a channel written without '#'
                // (or with different casing) still matches the server's channel name.
                chanValue = NormalizeKeyName(chanValue);

                try
                {
                    var plainKey = SecureConfig.Decrypt(encKey);

                    if (!string.IsNullOrWhiteSpace(plainKey))
                    {
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

                    // Normalize exactly like section 1 / SetChannelKey so a chat_keys
                    // entry stored without a leading '#' still matches the channel the
                    // message arrives on. Without this, such channels show up encrypted
                    // after every reconnect until the key is re-applied by hand.
                    channel = NormalizeKeyName(channel);

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
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(utf8Key))
            return;

        channel = NormalizeKeyName(channel);

        try
        {
            lock (fishLock)
            {
                fishDecryptors[channel] = new FishDecryptor(utf8Key);
            }

            if (!persist)
                return;

            if (siteConfig.SiteSettings.ChatKeys == null)
                siteConfig.SiteSettings.ChatKeys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var encKey = SecureConfig.Encrypt(utf8Key);
            siteConfig.SiteSettings.ChatKeys[channel] = encKey;

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

    public string GetChannelKey(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel) || siteConfig?.SiteSettings == null)
            return string.Empty;

        channel = NormalizeKeyName(channel);
        var siteSettings = siteConfig.SiteSettings;

        if (channel.StartsWith("PM:", StringComparison.OrdinalIgnoreCase))
        {
            string pmNick = channel.Substring(3);
            lock (fishLock)
            {
                if (pmFishKeys.TryGetValue(pmNick, out var pmKey))
                    return pmKey;
            }

            if (siteSettings.ChatKeys != null)
            {
                foreach (var kvp in siteSettings.ChatKeys)
                {
                    if (!string.Equals(NormalizeKeyName(kvp.Key), channel, StringComparison.OrdinalIgnoreCase))
                        continue;

                    return SecureConfig.Decrypt(kvp.Value) ?? string.Empty;
                }
            }

            return string.Empty;
        }

        if (siteSettings.ChatKeys != null)
        {
            foreach (var kvp in siteSettings.ChatKeys)
            {
                if (!string.Equals(NormalizeKeyName(kvp.Key), channel, StringComparison.OrdinalIgnoreCase))
                    continue;

                return SecureConfig.Decrypt(kvp.Value) ?? string.Empty;
            }
        }

        for (int i = 1; i <= 20; i++)
        {
            var chanProp = siteSettings.GetType().GetProperty($"Chan{i}");
            var keyProp = siteSettings.GetType().GetProperty($"BlowfishKey{i}");
            var chanValue = chanProp?.GetValue(siteSettings) as string;

            if (!string.Equals(NormalizeKeyName(chanValue), channel, StringComparison.OrdinalIgnoreCase))
                continue;

            var encKey = keyProp?.GetValue(siteSettings) as string;
            return string.IsNullOrWhiteSpace(encKey) ? string.Empty : SecureConfig.Decrypt(encKey) ?? string.Empty;
        }

        return string.Empty;
    }

    private static string NormalizeKeyName(string channel)
    {
        if (string.IsNullOrWhiteSpace(channel))
            return string.Empty;

        channel = channel.Trim();

        if (!channel.StartsWith("#") && !channel.StartsWith("PM:", StringComparison.OrdinalIgnoreCase))
            channel = "#" + channel.TrimStart('#');

        return channel;
    }

    public void SetChannelBlowfishKey(string channel, string utf8Key)
    {
        if (string.IsNullOrWhiteSpace(channel) || string.IsNullOrWhiteSpace(utf8Key))
            return;

        try
        {
            channel = NormalizeKeyName(channel);

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

    private void SafeTabbedLogAction(Action<IChannelOutput> action)
    {
        bool trackingEnabled;
        lock (userTrackingLock)
        {
            trackingEnabled = enableUserTracking;
        }

        if (!trackingEnabled)
            return;

        if (tabbedLogOutput == null || tabbedLogOutput.IsDisposed)
            return;

        // The WinForms version had to marshal to the UI thread here
        // (InvokeRequired/BeginInvoke). Thread affinity is a UI concern, so
        // IChannelOutput implementations now handle their own synchronisation and we
        // call straight through — one less hop on the IRC receive path.
        try
        {
            action(tabbedLogOutput);
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _disconnecting, 1) == 1)
            return;

        try
        {
            lock (userTrackingLock)
            {
                enableUserTracking = false;
            }

            lock (userTrackingLock)
            {
                channelUsers.Clear();
                pendingNamesLists.Clear();
            }

            lock (userTrackingLock)
            {
                channelUserPrefixes.Clear();
            }

            try { localCancellationTokenSource?.Cancel(); } catch { }

            // Abort socket I/O to break ReadAsync
            try { currentTcpClient?.Close(); } catch { }

            // Do NOT wait on listeningTask here
            // Do NOT close/dispose currentSslStream here

            lock (fishLock)
            {
                fishDecryptors.Clear();
                pmFishKeys.Clear();
                pendingKeyExchanges.Clear();
            }

            LogManager.LogIRC(IRCEventType.Disconnection, $"Disconnect requested for {siteName}");
        }
        catch (Exception ex)
        {
            try { LogManager.LogIRC(IRCEventType.Error, $"Error during disconnect request: {ex.Message}"); } catch { }
        }
    }

    public async Task ConnectToZNCAsync()
    {

        Interlocked.Exchange(ref _disconnecting, 0);

        // Disconnect() clears fishDecryptors/pmFishKeys/pendingKeyExchanges; reload the
        // channel keys so a reconnect on the same instance can still decrypt (PM key
        // exchanges necessarily have to be renegotiated by the peer).
        LoadChannelKeys(siteConfig);
        lock (userTrackingLock)
        {
            connectedStatusChannels.Clear();
        }

        TcpClient tcpClient = null;
        SslStream sslStream = null;

        try
        {
            localCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            tcpClient = new TcpClient();
            await tcpClient.ConnectAsync(host, port);
            this.currentTcpClient = tcpClient; // ← STORE IT

            AppendOutput($"[INFO] Connected to IRC server: {host}:{port}", Color.Green);

            sslStream = new SslStream(tcpClient.GetStream(), false,
                (sender, certificate, chain, sslPolicyErrors) => true);

            this.currentSslStream = sslStream;

            await sslStream.AuthenticateAsClientAsync(host);
            AppendOutput("[INFO] SSL/TLS authentication successful.", Color.Green);

            // PASS must be sent BEFORE NICK/USER (RFC 1459 / ZNC) — after registration
            // completes the server rejects it with 462 ERR_ALREADYREGISTRED.
            await SendMessageAsync(sslStream, $"PASS {zncPassword}");
            await SendMessageAsync(sslStream, $"NICK {ircNick}");
            await SendMessageAsync(sslStream, $"USER {ircNick} 0 * :{ircNick}");

            var isRegistered = await WaitForRegistrationOrTimeoutAsync(sslStream, tcpClient, TimeSpan.FromSeconds(10));
            if (!isRegistered)
            {
                AppendOutput("[WARN] IRC/ZNC registration was not confirmed; skipping channel JOINs to avoid joining the wrong network.", Color.Orange);
            }
            else
            {
                foreach (var channel in channels)
                {
                    if (string.IsNullOrWhiteSpace(channel))
                        continue;

                    await SendMessageAsync(sslStream, $"JOIN {channel}");

                    if (chatOnlyMode)
                    {
                        AppendOutput($"[INFO] Joined channel for chat: {channel}", Color.Green);
                    }
                    else
                    {
                        AppendOutput($"[INFO] Monitoring channel for chat: {channel}", Color.Cyan);
                    }
                }

                // Ask for the member list explicitly.
                //
                // JOIN only produces RPL_NAMREPLY when the server actually processes a
                // *new* join. Against ZNC we are usually already in the channel from a
                // previous attach, so the JOIN is absorbed and no 353/366 ever arrives —
                // which left the user list permanently empty. An explicit NAMES always
                // answers. (The WinForms build got this for free: opening the chat window
                // called SetUserTrackingEnabled(true) on an already-connected client,
                // which fires RequestUserList().)
                foreach (var channel in channels)
                {
                    if (string.IsNullOrWhiteSpace(channel))
                        continue;

                    await SendMessageAsync(sslStream, $"NAMES {channel}");
                }
            }

            listeningTask = ListenForMessagesAsync(sslStream);

            if (isRegistered)
                _ = RefreshUserListAfterConnectAsync();

            await listeningTask;
        }
        catch (OperationCanceledException)
        {
            AppendOutput($"Connection canceled for Site {siteName}.", Color.Orange);
        }
        catch (Exception ex)
        {
            AppendOutput($"Error connecting to IRC server for Site {siteName}: {ex.Message}", Color.Red);
        }
        finally
        {
            try { localCancellationTokenSource?.Cancel(); } catch { }

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

    private async Task<bool> WaitForRegistrationOrTimeoutAsync(
        SslStream sslStream,
        TcpClient tcpClient,
        TimeSpan timeout)
    {
        var buffer = new byte[4096];
        var start = DateTime.UtcNow;

        while (!localCancellationTokenSource.Token.IsCancellationRequested &&
               DateTime.UtcNow - start < timeout)
        {
            if (!sslStream.CanRead || !tcpClient.Connected)
                break;

            if (tcpClient.Available == 0)
            {
                await Task.Delay(100, localCancellationTokenSource.Token);
                continue;
            }

            int bytesRead;
            try
            {
                bytesRead = await sslStream.ReadAsync(buffer, 0, buffer.Length, localCancellationTokenSource.Token);
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

            string text = Encoding.UTF8.GetString(buffer, 0, bytesRead);
            var lines = text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd('\r', '\n');
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
                {
                    var payload = line.Substring(4).TrimStart(':', ' ');
                    await SendMessageAsync(sslStream, $"PONG :{payload}");
                    continue;
                }

                // ZNC reports an unknown/invalid network via a *status notice. If the
                // configured network doesn't exist, ZNC would otherwise silently drop
                // us on the default network and we'd JOIN the wrong channels there.
                // Treat this as a failed registration so JOINs are skipped.
                if (IsZncBadNetworkNotice(line))
                {
                    AppendOutput(
                        $"[ERROR] ZNC rejected network '{expectedNetwork}' for {siteName}: {line}. " +
                        "Check the site's Username (user/network) or Network field. Skipping channel JOINs.",
                        Color.Red);
                    return false;
                }

                if (line.Contains(" 001 ") || line.Contains(" 376 ") || line.Contains(" 422 "))
                {
                    if (string.IsNullOrWhiteSpace(expectedNetwork))
                    {
                        AppendOutput(
                            $"[WARN] No ZNC network configured for {siteName}; connecting to ZNC's default network. " +
                            "If channels appear on the wrong network, set the Network field or use 'user/network'.",
                            Color.Orange);
                    }
                    else
                    {
                        AppendOutput(
                            $"[INFO] IRC/ZNC registration complete for {siteName} (network '{expectedNetwork}'), joining configured chat channels.",
                            Color.Green);
                    }
                    return true;
                }
            }
        }

        return false;
    }

    // Recognizes the ZNC *status notices emitted when the requested network is
    // missing or not selected. Kept broad but anchored to ZNC's *status source so
    // ordinary channel traffic can't trip it.
    private static bool IsZncBadNetworkNotice(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var fromStatus = line.IndexOf("*status", StringComparison.OrdinalIgnoreCase) >= 0;
        if (!fromStatus)
            return false;

        var lower = line.ToLowerInvariant();
        return lower.Contains("network")
            && (lower.Contains("doesn't exist")
                || lower.Contains("does not exist")
                || lower.Contains("unknown network")
                || lower.Contains("no such network"));
    }

    private async Task RefreshUserListAfterConnectAsync()
    {
        try
        {
            await Task.Delay(1500, localCancellationTokenSource.Token);
            await RequestUserList();
        }
        catch (OperationCanceledException)
        {
            // Normal when chat disconnects during startup.
        }
        catch (Exception ex)
        {
            AppendOutput($"[WARN] Failed to refresh chat user list for {siteName}: {ex.Message}", Color.Orange);
        }
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

                    await ProcessIrcUserTracking(line);
                }

                messageBuilder.Clear();
                messageBuilder.Append(lines[lines.Length - 1]);
            }
        }
        catch (OperationCanceledException)
        {
            AppendOutput($"Listening loop canceled for Site {siteName}.", Color.Orange);
        }
        catch (Exception ex)
        {
            AppendOutput($"Error while listening for messages: {ex.Message}", Color.Red);
        }
    }

    /// <summary>Channel status mode letter to its nick prefix, '\0' if it is not one.</summary>
    private static char PrefixForMode(char mode)
    {
        switch (mode)
        {
            case 'q': return '~';   // owner
            case 'a': return '&';   // admin / protected
            case 'o': return '@';   // op
            case 'h': return '%';   // half-op
            case 'v': return '+';   // voice
            default: return '\0';
        }
    }

    /// <summary>
    /// Rebuilds the prefixed member list for a channel and pushes it to the UI.
    /// Used after MODE changes, where only a single nick moved but the consumer wants a
    /// consistent snapshot rather than an incremental patch it has to merge itself.
    /// </summary>
    private void PushUserList(string channel)
    {
        List<string> raw;

        lock (userTrackingLock)
        {
            if (!channelUsers.TryGetValue(channel, out var members))
                return;

            channelUserPrefixes.TryGetValue(channel, out var prefixes);

            raw = members
                .Select(n => prefixes != null && prefixes.TryGetValue(n, out var p) ? p + n : n)
                .ToList();
        }

        SafeTabbedLogAction(t => t.UpdateUserList(siteName, channel, raw));
    }

    private void TrackObservedChannelUser(string channel, string username)
    {
        var shouldAdd = false;

        lock (userTrackingLock)
        {
            if (!channelUsers.ContainsKey(channel))
                channelUsers[channel] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            shouldAdd = channelUsers[channel].Add(username);
        }

        if (shouldAdd)
            SafeTabbedLogAction(t => t.AddUser(siteName, channel, username));
    }

    private string NormalizeNick(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        var nick = raw;

        int bangIdx = nick.IndexOf('!');
        if (bangIdx >= 0)
            nick = nick.Substring(0, bangIdx);

        int slashIdx = nick.IndexOf('/');
        if (slashIdx >= 0)
            nick = nick.Substring(0, slashIdx);

        return nick;
    }

    private async Task ProcessIrcUserTracking(string line)
    {
        bool trackingEnabled;
        lock (userTrackingLock)
        {
            trackingEnabled = enableUserTracking;
        }

        if (!trackingEnabled)
            return;

        if (tabbedLogOutput == null || tabbedLogOutput.IsDisposed)
            return;

        try
        {
            // JOIN
            var joinMatch = Regex.Match(line, @"^:(\S+)!\S+@\S+ JOIN :?(\S+)$");
            if (joinMatch.Success)
            {
                string rawUsername = joinMatch.Groups[1].Value;
                string username = NormalizeNick(rawUsername);
                string channel = joinMatch.Groups[2].Value;
                string thisNick = NormalizeNick(this.ircNick);

                if (username.Equals(thisNick, StringComparison.OrdinalIgnoreCase))
                    return;

                lock (userTrackingLock)
                {
                    if (!channelUsers.ContainsKey(channel))
                        channelUsers[channel] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (!channelUsers[channel].Add(username))
                        return; // already known
                }

                SafeTabbedLogAction(t => t.AddUser(siteName, channel, username));
                SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, channel, $"*** {username} has joined {channel}", Color.Gray));
                return;
            }

            // PART
            var partMatch = Regex.Match(line, @":(\S+)!\S+@\S+ PART (\S+)");
            if (partMatch.Success)
            {
                string rawUsername = partMatch.Groups[1].Value;
                string username = NormalizeNick(rawUsername);
                string channel = partMatch.Groups[2].Value;

                lock (userTrackingLock)
                {
                    // also remove from channelUsers, otherwise a re-join is ignored
                    // by the JOIN handler's "already known" guard forever
                    if (channelUsers.TryGetValue(channel, out var users))
                        users.Remove(username);

                    if (channelUserPrefixes.ContainsKey(channel))
                        channelUserPrefixes[channel].Remove(username);
                }

                SafeTabbedLogAction(t => t.RemoveUser(siteName, channel, username));
                SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, channel, $"*** {username} has left {channel}", Color.Gray));
                return;
            }

            // QUIT
            var quitMatch = Regex.Match(line, @":(\S+)!\S+@\S+ QUIT");
            if (quitMatch.Success)
            {
                string rawUsername = quitMatch.Groups[1].Value;
                string username = NormalizeNick(rawUsername);

                var quitChannels = new List<string>();
                lock (userTrackingLock)
                {
                    foreach (var channel in channelUsers.Keys.ToList())
                    {
                        if (channelUsers[channel].Remove(username))
                        {
                            if (channelUserPrefixes.ContainsKey(channel))
                                channelUserPrefixes[channel].Remove(username);
                            quitChannels.Add(channel);
                        }
                    }
                }

                foreach (var channel in quitChannels)
                {
                    SafeTabbedLogAction(t => t.RemoveUser(siteName, channel, username));
                    SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, channel, $"*** {username} has quit", Color.Gray));
                }
                return;
            }

            // NICK
            var nickMatch = Regex.Match(line, @":(\S+)!\S+@\S+ NICK :?(\S+)");
            if (nickMatch.Success)
            {
                string rawOldNick = nickMatch.Groups[1].Value;
                string oldNick = NormalizeNick(rawOldNick);
                string newNick = NormalizeNick(nickMatch.Groups[2].Value);

                var renamedChannels = new List<string>();
                lock (userTrackingLock)
                {
                    foreach (var channel in channelUsers.Keys.ToList())
                    {
                        if (channelUsers[channel].Remove(oldNick))
                        {
                            channelUsers[channel].Add(newNick);

                            // 🔹 Also move the prefix (~ & @ % +) from oldNick → newNick
                            if (channelUserPrefixes.TryGetValue(channel, out var prefixes) &&
                                prefixes.TryGetValue(oldNick, out var prefixChar))
                            {
                                prefixes.Remove(oldNick);
                                prefixes[newNick] = prefixChar;
                            }

                            renamedChannels.Add(channel);
                        }
                    }
                }

                foreach (var channel in renamedChannels)
                {
                    SafeTabbedLogAction(t => t.RemoveUser(siteName, channel, oldNick));
                    SafeTabbedLogAction(t => t.AddUser(siteName, channel, newNick));
                    SafeTabbedLogAction(t => t?.AppendChannelMessage(
                        siteName,
                        channel,
                        $"*** {oldNick} is now known as {newNick}",
                        Color.Gray
                    ));
                }

                string oldPmKey = $"PM:{oldNick}";
                string newPmKey = $"PM:{newNick}";

                lock (fishLock)
                {
                    if (fishDecryptors.ContainsKey(oldPmKey))
                    {
                        fishDecryptors[newPmKey] = fishDecryptors[oldPmKey];
                        fishDecryptors.Remove(oldPmKey);
                    }

                    if (pmFishKeys.ContainsKey(oldNick))
                    {
                        pmFishKeys[newNick] = pmFishKeys[oldNick];
                        pmFishKeys.Remove(oldNick);
                    }

                    if (pendingKeyExchanges.ContainsKey(oldNick))
                    {
                        pendingKeyExchanges[newNick] = pendingKeyExchanges[oldNick];
                        pendingKeyExchanges.Remove(oldNick);
                    }
                }

                return;
            }

            // MODE — keeps the user list ranks live.
            // Without this a +o/-o after join is invisible until the next NAMES, so
            // someone opped an hour ago still shows as a plain user.
            var modeMatch = Regex.Match(line, @"^:(\S+)!\S+@\S+ MODE (#\S+) (\S+)(?:\s+(.*))?$");
            if (modeMatch.Success)
            {
                string channel = modeMatch.Groups[2].Value;
                string modes = modeMatch.Groups[3].Value;
                var modeArgs = modeMatch.Groups[4].Value
                    .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

                bool touched = false;
                bool adding = true;
                int argIndex = 0;

                lock (userTrackingLock)
                {
                    if (!channelUserPrefixes.ContainsKey(channel))
                        channelUserPrefixes[channel] = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);

                    foreach (var m in modes)
                    {
                        if (m == '+') { adding = true; continue; }
                        if (m == '-') { adding = false; continue; }

                        char prefix = PrefixForMode(m);

                        // Only status modes consume a nick argument here. Other
                        // argument-taking modes (k, l, b, e, I) would desync the index,
                        // so anything unknown stops the walk instead of guessing.
                        if (prefix == '\0')
                        {
                            if ("klbeI".IndexOf(m) >= 0) break;
                            continue;
                        }

                        if (argIndex >= modeArgs.Length) break;

                        string nick = NormalizeNick(modeArgs[argIndex++]);

                        if (!channelUsers.ContainsKey(channel))
                            channelUsers[channel] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        channelUsers[channel].Add(nick);

                        if (adding)
                            channelUserPrefixes[channel][nick] = prefix;
                        else
                            channelUserPrefixes[channel].Remove(nick);

                        touched = true;
                    }
                }

                if (touched)
                    PushUserList(channel);

                return;
            }

            // NAMES
            var namesMatch = Regex.Match(line, @":(\S+) 353 \S+ . (\S+) :(.+)");
            if (namesMatch.Success)
            {
                string channel = namesMatch.Groups[2].Value;
                string userList = namesMatch.Groups[3].Value;

                var users = userList.Split(' ').Where(u => !string.IsNullOrWhiteSpace(u)).ToList();

                lock (userTrackingLock)
                {
                    if (!pendingNamesLists.ContainsKey(channel))
                        pendingNamesLists[channel] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var user in users)
                    {
                        pendingNamesLists[channel].Add(user);
                    }
                }

                return;
            }

            // END OF NAMES (366)
            var endOfNamesMatch = Regex.Match(line, @":(\S+) 366 \S+ (\S+)");
            if (endOfNamesMatch.Success)
            {
                string channel = endOfNamesMatch.Groups[2].Value;

                List<string> allUsersRaw = null;
                var showConnectedStatus = false;

                lock (userTrackingLock)
                {
                    showConnectedStatus = connectedStatusChannels.Add(channel);

                    if (pendingNamesLists.ContainsKey(channel))
                    {
                        allUsersRaw = pendingNamesLists[channel].ToList();

                        if (!channelUsers.ContainsKey(channel))
                            channelUsers[channel] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                        if (!channelUserPrefixes.ContainsKey(channel))
                            channelUserPrefixes[channel] = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);

                        channelUsers[channel].Clear();
                        channelUserPrefixes[channel].Clear();

                        foreach (var userRaw in allUsersRaw)
                        {
                            if (string.IsNullOrWhiteSpace(userRaw))
                                continue;

                            var u = userRaw.Trim();

                            char prefix = '\0';
                            if ("~&@%+".IndexOf(u[0]) >= 0)
                            {
                                prefix = u[0];
                                u = u.Substring(1); // strip prefix from display nick
                            }

                            string cleanUser = NormalizeNick(u);
                            channelUsers[channel].Add(cleanUser);

                            if (prefix != '\0')
                            {
                                channelUserPrefixes[channel][cleanUser] = prefix;
                            }
                        }

                        pendingNamesLists.Remove(channel);
                    }
                }

                if (allUsersRaw != null)
                {
                    // still send the raw list (with prefixes) to the UI for the user list
                    SafeTabbedLogAction(t => t.UpdateUserList(siteName, channel, allUsersRaw));
                }

                if (showConnectedStatus)
                {
                    SafeTabbedLogAction(t => t.AppendChannelMessage(siteName, channel, $"*** Connected to {channel}", Color.Green));
                }

                return;
            }

            // DH1080_INIT
            if (line.Contains("DH1080_INIT") && (line.Contains("PRIVMSG") || line.Contains("NOTICE")))
            {
                var dhMatch = Regex.Match(line, @":(\S+)!\S+@\S+ (PRIVMSG|NOTICE) (\S+) :DH1080_INIT (.+)");
                if (dhMatch.Success)
                {
                    string rawUsername = dhMatch.Groups[1].Value;
                    string username = NormalizeNick(rawUsername);
                    // CBC-mode FiSH clients send "DH1080_INIT <pubkey> CBC" — keep only the key
                    string theirPublicKeyRaw = dhMatch.Groups[4].Value.Trim().Split(' ')[0];

                    string thisNick = NormalizeNick(this.ircNick);
                    if (username.Equals(thisNick, StringComparison.OrdinalIgnoreCase))
                        return;

                    try
                    {
                        var dh = new DH1080();
                        string ourPublicKey = dh.GetPublicKey();
                        string sharedSecret = dh.ComputeSharedSecret(theirPublicKeyRaw);

                        lock (fishLock)
                        {
                            pmFishKeys[username] = sharedSecret;
                            fishDecryptors[$"PM:{username}"] = new FishDecryptor(sharedSecret);
                        }

                        await SendMessageAsync(currentSslStream, $"NOTICE {username} :DH1080_FINISH {ourPublicKey}");

                        SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, $"PM:{username}",
                            $"[FiSH] Received key exchange from {username}, replied with DH1080_FINISH", Color.Yellow));
                        SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, $"PM:{username}",
                            "[FiSH] ✓ Key exchange completed! Messages are now encrypted.", Color.Green));
                        SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, $"PM:{username}",
                            $"[FiSH] Key for {username} set to *censored* (CBC Mode)", Color.Green));
                    }
                    catch (Exception ex)
                    {
                        SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, $"PM:{username}", $"[ERROR] Failed to respond to key exchange: {ex.Message}", Color.Red));
                    }
                    return;
                }
            }

            // DH1080_FINISH
            if (line.Contains("DH1080_FINISH") && (line.Contains("PRIVMSG") || line.Contains("NOTICE")))
            {
                var dhMatch = Regex.Match(line, @":(\S+)!\S+@\S+ (PRIVMSG|NOTICE) (\S+) :DH1080_FINISH (.+)");
                if (dhMatch.Success)
                {
                    string rawUsername = dhMatch.Groups[1].Value;
                    string username = NormalizeNick(rawUsername);
                    // CBC-mode FiSH clients append " CBC" — keep only the key
                    string theirPublicKeyRaw = dhMatch.Groups[4].Value.Trim().Split(' ')[0];

                    string thisNick = NormalizeNick(this.ircNick);
                    if (username.Equals(thisNick, StringComparison.OrdinalIgnoreCase))
                        return;

                    DH1080 pendingDh;
                    lock (fishLock)
                    {
                        pendingKeyExchanges.TryGetValue(username, out pendingDh);
                    }

                    if (pendingDh != null)
                    {
                        try
                        {
                            string sharedSecret = pendingDh.ComputeSharedSecret(theirPublicKeyRaw);

                            // Mutate all three dictionaries under the one lock. Never call
                            // back into the UI while holding it.
                            lock (fishLock)
                            {
                                pmFishKeys[username] = sharedSecret;
                                fishDecryptors[$"PM:{username}"] = new FishDecryptor(sharedSecret);
                                pendingKeyExchanges.Remove(username);
                            }

                            SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, $"PM:{username}",
                                "[FiSH] ✓ Key exchange completed! Messages are now encrypted.", Color.Green));
                            SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, $"PM:{username}",
                                $"[FiSH] Key for {username} set to *censored* (CBC Mode)", Color.Green));
                        }
                        catch (Exception ex)
                        {
                            SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, $"PM:{username}", $"[ERROR] Failed to complete key exchange: {ex.Message}", Color.Red));
                        }
                    }
                    return;
                }
            }

            // PRIVMSG
            var privmsgMatch = Regex.Match(line, @":(\S+)!\S+@\S+ PRIVMSG (\S+) :(.+)");
            if (privmsgMatch.Success)
            {
                string rawUsername = privmsgMatch.Groups[1].Value;
                string username = NormalizeNick(rawUsername);
                string target = privmsgMatch.Groups[2].Value;
                string message = privmsgMatch.Groups[3].Value;

                string thisNick = NormalizeNick(this.ircNick);
                if (username.Equals(thisNick, StringComparison.OrdinalIgnoreCase))
                    return;

                if (target.StartsWith("#"))
                {
                    Color msgColor = Color.White;
                    string decryptedMessage = message;

                    TrackObservedChannelUser(target, username);

                    FishDecryptor decryptor = null;
                    lock (fishLock)
                    {
                        fishDecryptors.TryGetValue(target, out decryptor);
                    }

                    if (IsDisconnecting)
                        return;

                    if (decryptor != null)
                    {
                        try
                        {
                            if (message.StartsWith("+OK ") || message.StartsWith("mcps"))
                            {
                                decryptedMessage = decryptor.DecryptMessage(message);
                                msgColor = Color.LightBlue;
                            }
                        }
                        catch
                        {
                            decryptedMessage = $"[DECRYPT ERROR] {message}";
                            msgColor = Color.Red;
                        }
                    }

                    // Get status prefix (if any)
                    string displayNick = username;
                    if (channelUserPrefixes.TryGetValue(target, out var prefixes) &&
                        prefixes.TryGetValue(username, out var prefixChar))
                    {
                        displayNick = prefixChar + username; // e.g. "@Finity"
                    }

                    SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, target, $"<{displayNick}> {decryptedMessage}", msgColor));
                }
                else
                {
                    string pmKey = $"PM:{username}";
                    string decryptedMessage = message;
                    Color msgColor = Color.Yellow;

                    // ⚠️ ADD LOCK HERE
                    FishDecryptor pmDecryptor = null;
                    lock (fishLock)
                    {
                        if (fishDecryptors.ContainsKey(pmKey))
                        {
                            fishDecryptors.TryGetValue(pmKey, out pmDecryptor);
                        }
                    }

                    if (IsDisconnecting)
                        return;

                    if (pmDecryptor != null && (message.StartsWith("+OK ") || message.StartsWith("mcps")))
                    {
                        try
                        {
                            decryptedMessage = pmDecryptor.DecryptMessage(message);
                            msgColor = Color.LightBlue;
                        }
                        catch
                        {
                            decryptedMessage = $"[DECRYPT ERROR] {message}";
                            msgColor = Color.Red;
                        }
                    }

                    SafeTabbedLogAction(t => t?.AppendChannelMessage(siteName, pmKey,
                        $"<{username}> {decryptedMessage}", msgColor));
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // Chat window was disposed, stop tracking silently
        }
        catch (InvalidOperationException)
        {
            // Handle was destroyed, stop tracking silently
        }
        catch (Exception ex)
        {
            if (!(ex is ObjectDisposedException) && !(ex is InvalidOperationException))
            {
                LogManager.LogIRC(IRCEventType.Error, $"User tracking error: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Sends a message to a specific channel/PM (tabbed chat)
    /// </summary>
    public async Task SendChannelMessage(string channel, string message)
    {
        if (currentSslStream == null)
        {
            AppendOutput("[ERROR] Not connected to IRC server", Color.Red);
            return;
        }

        try
        {
            string target = channel;
            string keyName = channel;

            if (channel.StartsWith("PM:", StringComparison.OrdinalIgnoreCase))
            {
                target = channel.Substring(3);
                keyName = $"PM:{target}";
            }

            string messageToSend = message;

            // ⚠️ ADD LOCK HERE
            FishDecryptor decryptor = null;
            lock (fishLock)
            {
                fishDecryptors.TryGetValue(keyName, out decryptor);
            }

            if (IsDisconnecting)
                return;

            if (decryptor != null)
            {
                try
                {
                    messageToSend = decryptor.EncryptMessage(message);
                }
                catch (Exception ex)
                {
                    tabbedLogOutput?.AppendChannelMessage(siteName, channel,
                        $"[ERROR] FiSH encryption failed: {ex.Message}", Color.Red);
                    return;
                }
            }
            else
            {
                tabbedLogOutput?.AppendChannelMessage(siteName, channel,
                    $"[DEBUG] No FiSH key found for {keyName}", Color.Orange);
            }

            await SendMessageAsync(currentSslStream, $"PRIVMSG {target} :{messageToSend}");

            // ... rest of code
        }
        catch (Exception ex)
        {
            AppendOutput($"[ERROR] Failed to send message: {ex.Message}", Color.Red);
        }
    }

    /// <summary>
    /// Initiates FiSH DH1080 key exchange with a user (chat)
    /// </summary>
    public async Task InitiateFishKeyExchange(string targetUser)
    {
        if (currentSslStream == null)
        {
            tabbedLogOutput?.AppendChannelMessage(siteName, $"PM:{targetUser}",
                "[ERROR] Not connected to IRC server", Color.Red);
            return;
        }

        try
        {
            var dh = new DH1080();
            string publicKey = dh.GetPublicKey();

            if (EngineSettings.DebugEnabled)
            {
                AppendOutput($"[DEBUG] Our public key being sent: {publicKey}", Color.Cyan);
                AppendOutput($"[DEBUG] Our public key length: {publicKey.Length}", Color.Cyan);
            }

            lock (fishLock)
            {
                pendingKeyExchanges[targetUser] = dh;
            }

            await SendMessageAsync(currentSslStream, $"NOTICE {targetUser} :DH1080_INIT {publicKey}");

            tabbedLogOutput?.AppendChannelMessage(siteName, $"PM:{targetUser}",
                "[FiSH] Sent DH1080_INIT, waiting for DH1080_FINISH...", Color.Yellow);
        }
        catch (Exception ex)
        {
            tabbedLogOutput?.AppendChannelMessage(siteName, $"PM:{targetUser}",
                $"[ERROR] FiSH key exchange failed: {ex.Message}", Color.Red);
        }
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
        catch (ObjectDisposedException) { }
        catch (IOException) { }
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




}
