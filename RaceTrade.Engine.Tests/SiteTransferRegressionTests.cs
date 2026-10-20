using Newtonsoft.Json.Linq;
using RaceTrade;
using RaceTrade.Engine.Security;
using RaceTrade.Web.Services;
using System.Security.Cryptography;

internal static class SiteTransferRegressionTests
{
    internal static void Run()
    {
        var previous = Environment.CurrentDirectory;
        var directory = Path.Combine(Path.GetTempPath(), "RaceTrade-site-transfer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Environment.CurrentDirectory = directory;
            SecureConfig.Configure(new SecretProtector(RandomNumberGenerator.GetBytes(32)));
            var store = new SiteStore();
            var transfer = new SiteTransferService(store);
            var site = SiteStore.NewSite("SOURCE");
            site.Server.Host = "irc.example.test";
            site.Server.Username = "test-user";
            site.Server.Password = "password-for-test";
            site.SiteSettings.FxpBackendId = "original-backend";
            site.SiteSettings.Chan1 = "#test";
            site.SiteSettings.BlowfishKey1 = "fish-test-key";
            site.SiteSettings.ChatKeys["#chat"] = SecureConfig.Encrypt("chat-test-key");
            site.Sections = new()
            {
                new Section
                {
                    IrcName = "TV", Rules = new() { "[release] contains INTERNAL DROP" },
                    Tags = new() { new Tag { MapFxpBackendSection = "TV-HD", TriggerRegex = "1080p" } }
                }
            };
            var key = store.Save(site);
            var publicExport = transfer.Export(new[] { key }, false);
            Check(!publicExport.Contains("test-user") && !publicExport.Contains("password-for-test") &&
                !publicExport.Contains("fish-test-key") && !publicExport.Contains("chat-test-key") && !publicExport.Contains("ENC2:"),
                "Public exports must exclude every credential, including encrypted chat keys.");
            var config = transfer.Parse(publicExport).Single();
            Check(config.Sections[0].Rules.Single() == "[release] contains INTERNAL DROP" &&
                config.Sections[0].Tags[0].MapFxpBackendSection == "TV-HD", "Rules and mappings must survive export/import.");

            var privateExport = transfer.Export(new[] { key }, true);
            Check(privateExport.Contains("password-for-test") && privateExport.Contains("fish-test-key") && privateExport.Contains("chat-test-key"),
                "Explicit credential exports must be portable rather than machine-encrypted.");
            Check(SecureConfig.IsEncrypted(store.Load(key).Server.Password), "Export must not decrypt the saved configuration.");

            // Simulate importing on another machine with an unrelated local key.
            SecureConfig.Configure(new SecretProtector(RandomNumberGenerator.GetBytes(32)));
            config = transfer.Parse(privateExport).Single();
            var imported = transfer.Import(new[] { config }, "destination-backend", false, true);
            Check(imported.Imported == 1 && imported.Errors.Count == 0, "Portable import must succeed on another installation.");
            var destinationKey = store.ListNames().Single(n => n.StartsWith("destination-backend__", StringComparison.Ordinal));
            var saved = store.Load(destinationKey);
            Check(saved.SiteSettings.DisableSite && saved.SiteSettings.FxpBackendId == "destination-backend", "Imported sites must use the selected backend and disabled state.");
            Check(SecureConfig.Decrypt(saved.Server.Password) == "password-for-test" &&
                SecureConfig.Decrypt(saved.SiteSettings.BlowfishKey1) == "fish-test-key" &&
                SecureConfig.Decrypt(saved.SiteSettings.ChatKeys["#chat"]) == "chat-test-key", "All credentials must be encrypted with the destination key.");
            Check(transfer.Import(new[] { config }, "destination-backend", false, true).Skipped == 1,
                "Existing sites must be skipped by default, not duplicated or overwritten.");
            var stripped = transfer.Parse(publicExport).Single();
            transfer.Import(new[] { stripped }, "destination-backend", true, true);
            saved = store.Load(destinationKey);
            Check(SecureConfig.Decrypt(saved.Server.Password) == "password-for-test" && saved.Server.Username == "test-user" &&
                SecureConfig.Decrypt(saved.SiteSettings.ChatKeys["#chat"]) == "chat-test-key", "Replacing from a public export must preserve missing credentials.");

            Reject(() => transfer.Export(new[] { "../outside" }, false));
            Reject(() => transfer.Parse("{'site_settings':{'sitename':'../../outside'}}"));
            Reject(() => transfer.Parse("{'format':'RaceTrade-sites','version':2,'sites':[]}"));
            Reject(() => transfer.Parse("{'site_settings':{'sitename':'SAFE','sitename':'OTHER'}}"));
            Reject(() => transfer.Import(new[] { config, config }, "destination-backend", true, true));
            Reject(() => transfer.Import(new[] { config }, "", false, false));
            Check(store.ListNames().Count == 2, "Rejected imports must not create extra files.");
            var raw = JObject.Parse(publicExport)["sites"]![0]!.ToString();
            Check(transfer.Parse(raw).Count == 1, "Single-site configuration JSON must also be accepted.");

            var backendSite = new FxpBackendSite
            {
                Name = "BACKEND-SITE", Affils = new() { " TEAM-A ", "team-a", "", null, "TEAM-B" },
                Sections = new() { new FxpBackendSection { Name = "TV", Path = "/TV" } }
            };
            var backendImport = store.ImportFromFxpBackendSites(new[] { backendSite }, false, "backend-import");
            Check(backendImport.Imported == 1 && backendImport.Errors == 0, "Backend site import must succeed.");
            var backendKey = store.ListNames().Single(n => n.StartsWith("backend-import__", StringComparison.Ordinal));
            Check(store.Load(backendKey).Affils.SequenceEqual(new[] { "TEAM-A", "TEAM-B" }),
                "Backend imports must copy affils, trim names and remove case-insensitive duplicates.");
            backendSite.Affils = new() { "NEW-TEAM" };
            Check(store.ImportFromFxpBackendSites(new[] { backendSite }, false, "backend-import").Skipped == 1 &&
                store.Load(backendKey).Affils.SequenceEqual(new[] { "TEAM-A", "TEAM-B" }), "Skipping an existing site must preserve its affils.");
            store.ImportFromFxpBackendSites(new[] { backendSite }, true, "backend-import");
            Check(store.Load(backendKey).Affils.SequenceEqual(new[] { "NEW-TEAM" }), "Replacing a backend site must import its updated affils.");
        }
        finally
        {
            Environment.CurrentDirectory = previous;
            Directory.Delete(directory, true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (Exception) { return; }
        throw new InvalidOperationException("Invalid import/export input was accepted.");
    }
}
