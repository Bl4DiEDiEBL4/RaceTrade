using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RaceTrade.Engine.Security;

namespace RaceTrade
{
    /// <summary>
    /// Converts secrets written by the old WinForms build from DPAPI "ENC:" to the
    /// portable v2 "ENC2:" format. This must run on Windows under the same account
    /// that created the old files; DPAPI cannot be decrypted elsewhere.
    /// </summary>
    public static class LegacySecretMigrator
    {
        private const string LegacyPrefix = "ENC:";
        private static readonly byte[] Entropy =
            Encoding.UTF8.GetBytes("RaceTrade.SecureConfig.v1.entropy");

        public static LegacySecretMigrationResult MigrateDataRoot(string dataRoot)
        {
            if (!OperatingSystem.IsWindows())
                throw new PlatformNotSupportedException(
                    "Legacy ENC: secrets use Windows DPAPI. Run this migration on the Windows account that created the old configs.");

            if (string.IsNullOrWhiteSpace(dataRoot))
                throw new ArgumentException("Data root is required.", nameof(dataRoot));

            var result = new LegacySecretMigrationResult();
            if (!Directory.Exists(dataRoot))
                return result;

            foreach (var file in Directory.GetFiles(dataRoot, "*.json", SearchOption.AllDirectories))
            {
                if (file.EndsWith(".bak", StringComparison.OrdinalIgnoreCase))
                    continue;

                result.FilesScanned++;

                try
                {
                    var json = File.ReadAllText(file);
                    var token = JToken.Parse(json);
                    var migrated = MigrateToken(token, parentProperty: null, result);

                    if (!migrated)
                        continue;

                    AtomicFile.WriteAllText(file, token.ToString(Formatting.Indented));
                    result.FilesChanged++;
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"{file}: {ex.Message}");
                }
            }

            return result;
        }

        private static bool MigrateToken(JToken token, string parentProperty, LegacySecretMigrationResult result)
        {
            var changed = false;

            if (token is JObject obj)
            {
                foreach (var prop in obj.Properties())
                {
                    if (prop.Value.Type == JTokenType.String)
                    {
                        var value = prop.Value.Value<string>() ?? "";
                        if (IsLegacySecret(value) && IsSecretField(prop.Name, parentProperty))
                        {
                            var plain = DecryptLegacyDpapi(value);
                            prop.Value = SecureConfig.Encrypt(plain);
                            result.SecretsMigrated++;
                            changed = true;
                            continue;
                        }
                    }

                    if (MigrateToken(prop.Value, prop.Name, result))
                        changed = true;
                }
            }
            else if (token is JArray arr)
            {
                foreach (var child in arr)
                {
                    if (MigrateToken(child, parentProperty, result))
                        changed = true;
                }
            }

            return changed;
        }

        private static bool IsSecretField(string propertyName, string parentProperty)
        {
            if (propertyName.Equals("password", StringComparison.OrdinalIgnoreCase))
                return true;

            if (propertyName.IndexOf("blowfish", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;

            if (propertyName.Equals("tmdb_api_key", StringComparison.OrdinalIgnoreCase) ||
                propertyName.Equals("tmdb_key", StringComparison.OrdinalIgnoreCase) ||
                propertyName.Equals("tmdb_bearer_token", StringComparison.OrdinalIgnoreCase))
                return true;

            return parentProperty != null &&
                   parentProperty.Equals("chat_keys", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsLegacySecret(string value) =>
            !string.IsNullOrEmpty(value) &&
            value.StartsWith(LegacyPrefix, StringComparison.Ordinal) &&
            !value.StartsWith(SecretProtector.Prefix, StringComparison.Ordinal);

        private static string DecryptLegacyDpapi(string value)
        {
            var encrypted = Convert.FromBase64String(value.Substring(LegacyPrefix.Length));

            try
            {
#pragma warning disable CA1416 // Guarded by MigrateDataRoot: legacy DPAPI migration only runs on Windows.
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.CurrentUser));
#pragma warning restore CA1416
            }
            catch (CryptographicException)
            {
#pragma warning disable CA1416 // Guarded by MigrateDataRoot: legacy DPAPI migration only runs on Windows.
                return Encoding.UTF8.GetString(
                    ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
#pragma warning restore CA1416
            }
        }
    }

    public sealed class LegacySecretMigrationResult
    {
        public int FilesScanned { get; set; }
        public int FilesChanged { get; set; }
        public int SecretsMigrated { get; set; }
        public List<string> Errors { get; } = new List<string>();
    }
}
