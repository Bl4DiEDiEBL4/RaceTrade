using System;
using System.IO;
using RaceTrade.Engine.Security;

namespace RaceTrade
{
    /// <summary>
    /// Encryption façade for stored secrets, kept under the original name and API so the
    /// engine call sites compile unchanged.
    ///
    /// The WinForms implementation used Windows DPAPI (ProtectedData / CurrentUser).
    /// That type does not exist on net8.0 without a Windows-only package, and DPAPI also
    /// locked configs to a single Windows user on a single machine. All real work now
    /// happens in <see cref="SecretProtector"/> (AES-256-GCM with a local key file),
    /// which behaves identically on Windows, Linux and macOS.
    ///
    /// MIGRATION: values written by the old build carry the "ENC:" prefix and can only be
    /// read on the Windows account that wrote them. Convert them on that machine before
    /// moving configs elsewhere. Decrypting an "ENC:" value here throws with an
    /// explanatory message rather than silently returning garbage.
    ///
    /// The bulk config-rewriting helpers from the old class (EncryptConfigFile,
    /// EncryptAllSiteConfigs, EncryptMainConfig, EncryptPreBotConfigs) were one-off
    /// maintenance tools driven from menu items; they are intentionally not part of the
    /// headless engine.
    /// </summary>
    public static class SecureConfig
    {
        private static readonly object InitLock = new object();
        private static SecretProtector _protector;

        /// <summary>
        /// Where the AES key lives. Anyone who can read this file can read every stored
        /// secret, so it is created with owner-only permissions. Set this before first
        /// use to relocate it (e.g. outside a cloud-synced folder).
        /// </summary>
        public static string KeyFilePath { get; set; } =
            Path.Combine("userdata", "secret.key");

        private static SecretProtector Protector
        {
            get
            {
                if (_protector != null) return _protector;
                lock (InitLock)
                {
                    if (_protector == null)
                        _protector = SecretProtector.FromKeyFile(KeyFilePath);
                }
                return _protector;
            }
        }

        /// <summary>Lets a host inject its own protector (tests, custom key storage).</summary>
        public static void Configure(SecretProtector protector)
        {
            lock (InitLock)
            {
                _protector = protector;
            }
        }

        public static string Encrypt(string plainText) => Protector.Encrypt(plainText);

        public static string Decrypt(string encryptedText)
        {
            if (string.IsNullOrEmpty(encryptedText))
                return encryptedText;

            if (SecretProtector.IsLegacyDpapi(encryptedText))
            {
                // Fail loudly: returning the ciphertext would resurface much later as a
                // confusing login failure against a site or cbftp.
                LogManager.Error(
                    "A secret is still in the old Windows DPAPI format (ENC:). Convert it on the " +
                    "Windows machine that created it before using this config here.");
                throw new InvalidOperationException(
                    "Secret is in the legacy Windows DPAPI format (ENC:) and cannot be read on this platform.");
            }

            return Protector.Decrypt(encryptedText);
        }

        public static bool IsEncrypted(string text) => SecretProtector.IsEncrypted(text);

        public static string EncryptIfNeeded(string text) => Protector.EncryptIfNeeded(text);
    }
}
