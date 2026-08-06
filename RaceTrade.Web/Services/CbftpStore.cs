using Newtonsoft.Json;

namespace RaceTrade.Web.Services;

/// <summary>
/// Reads and writes cbftp/cbftp_config.json - the web equivalent of the WinForms
/// AddCbftp form. Same file, same shape, so the WinForms build and this one stay
/// interchangeable while both exist.
/// </summary>
public sealed class CbftpStore
{
    private const string Path_ = "cbftp/cbftp_config.json";

    public Config Load()
    {
        if (!File.Exists(Path_)) return new Config();

        var cfg = JsonConvert.DeserializeObject<Config>(File.ReadAllText(Path_)) ?? new Config();
        cfg.CbftpServers ??= new List<CbftpServer>();
        cfg.Jobs ??= new JobSettings();
        return cfg;
    }

    /// <summary>
    /// Saves the config, encrypting any password still held in plaintext so a value
    /// typed into the browser never reaches disk in the clear.
    /// </summary>
    public void Save(Config cfg)
    {
        Directory.CreateDirectory("cbftp");

        foreach (var s in cfg.CbftpServers ?? new List<CbftpServer>())
            s.Password = SecureConfig.EncryptIfNeeded(s.Password);

        AtomicFile.WriteAllText(Path_, JsonConvert.SerializeObject(cfg, Formatting.Indented));

        // The racer caches server config at startup; reload so edits take effect now.
        CbftpRacer.ReloadConfiguration();
        LogManager.Success("Saved cbftp servers.");
    }
}
