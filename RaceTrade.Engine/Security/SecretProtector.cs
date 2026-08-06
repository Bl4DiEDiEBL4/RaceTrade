using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace RaceTrade.Engine.Security
{
    /// <summary>
    /// Encrypts secrets (site passwords, cbftp passwords, Blowfish keys) at rest.
    ///
    /// WHY THIS REPLACES THE OLD SecureConfig:
    /// The WinForms app used Windows DPAPI (ProtectedData, CurrentUser scope). That is
    /// Windows-only — it throws PlatformNotSupportedException on Linux — and it also
    /// bound configs to one Windows user on one machine, so a sites\ folder could never
    /// be copied anywhere, not even to another Windows box.
    ///
    /// This uses AES-256-GCM with a locally stored key file:
    ///   - works identically on Windows, Linux and macOS
    ///   - configs can be moved between machines by copying the key file with them
    ///   - the key file is created with owner-only permissions
    ///
    /// Format: "ENC2:" + base64(nonce[12] || ciphertext || tag[16])
    ///
    /// Values written by the old Windows build ("ENC:") are NOT readable here. Run the
    /// migration on Windows (see SecureConfigMigration) while DPAPI is still available;
    /// it rewrites them into this format.
    /// </summary>
    public sealed class SecretProtector
    {
        public const string Prefix = "ENC2:";
        private const string LegacyPrefix = "ENC:";

        private const int NonceSize = 12;   // AES-GCM standard nonce
        private const int TagSize = 16;     // AES-GCM tag
        private const int KeySize = 32;     // AES-256

        private readonly byte[] _key;

        public SecretProtector(byte[] key)
        {
            if (key == null || key.Length != KeySize)
                throw new ArgumentException($"Key must be {KeySize} bytes.", nameof(key));
            _key = key;
        }

        /// <summary>
        /// Loads the key from <paramref name="keyFilePath"/>, creating it on first run.
        /// Keep this file safe: it decrypts every stored secret.
        /// </summary>
        public static SecretProtector FromKeyFile(string keyFilePath)
        {
            if (string.IsNullOrWhiteSpace(keyFilePath))
                throw new ArgumentException("Key file path required.", nameof(keyFilePath));

            var dir = Path.GetDirectoryName(keyFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            byte[] key;
            if (File.Exists(keyFilePath))
            {
                key = Convert.FromBase64String(File.ReadAllText(keyFilePath).Trim());
                if (key.Length != KeySize)
                    throw new CryptographicException($"Key file {keyFilePath} is corrupt (expected {KeySize} bytes).");
            }
            else
            {
                key = RandomNumberGenerator.GetBytes(KeySize);
                File.WriteAllText(keyFilePath, Convert.ToBase64String(key));
                RestrictToOwner(keyFilePath);
            }

            return new SecretProtector(key);
        }

        /// <summary>Owner-only file permissions (0600 on unix; ACL-inherited on Windows).</summary>
        private static void RestrictToOwner(string path)
        {
            try
            {
                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
            catch
            {
                // Non-fatal: the key still works, it is just more permissive than ideal.
            }
        }

        public static bool IsEncrypted(string value) =>
            !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

        public static bool IsLegacyDpapi(string value) =>
            !string.IsNullOrEmpty(value)
            && value.StartsWith(LegacyPrefix, StringComparison.Ordinal)
            && !value.StartsWith(Prefix, StringComparison.Ordinal);

        public string Encrypt(string plainText)
        {
            if (string.IsNullOrEmpty(plainText))
                return plainText;

            var plain = Encoding.UTF8.GetBytes(plainText);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];

            using (var aes = new AesGcm(_key, TagSize))
            {
                aes.Encrypt(nonce, plain, cipher, tag);
            }

            var payload = new byte[NonceSize + cipher.Length + TagSize];
            Buffer.BlockCopy(nonce, 0, payload, 0, NonceSize);
            Buffer.BlockCopy(cipher, 0, payload, NonceSize, cipher.Length);
            Buffer.BlockCopy(tag, 0, payload, NonceSize + cipher.Length, TagSize);

            return Prefix + Convert.ToBase64String(payload);
        }

        public string EncryptIfNeeded(string value) =>
            string.IsNullOrEmpty(value) || IsEncrypted(value) ? value : Encrypt(value);

        /// <summary>
        /// Decrypts a value. Plaintext (unprefixed) is returned unchanged so configs
        /// that were never encrypted keep working.
        /// </summary>
        public string Decrypt(string value)
        {
            if (string.IsNullOrEmpty(value))
                return value;

            if (IsLegacyDpapi(value))
            {
                throw new CryptographicException(
                    "This value is still in the old Windows DPAPI format (ENC:). " +
                    "Run the migration on the Windows machine that created it to convert it to ENC2:.");
            }

            if (!IsEncrypted(value))
                return value; // plaintext passthrough

            byte[] payload;
            try
            {
                payload = Convert.FromBase64String(value.Substring(Prefix.Length));
            }
            catch (FormatException)
            {
                // Not our ciphertext after all (e.g. a real password starting with ENC2:)
                return value;
            }

            if (payload.Length < NonceSize + TagSize)
                return value;

            var nonce = new byte[NonceSize];
            var tag = new byte[TagSize];
            var cipher = new byte[payload.Length - NonceSize - TagSize];

            Buffer.BlockCopy(payload, 0, nonce, 0, NonceSize);
            Buffer.BlockCopy(payload, NonceSize, cipher, 0, cipher.Length);
            Buffer.BlockCopy(payload, NonceSize + cipher.Length, tag, 0, TagSize);

            var plain = new byte[cipher.Length];
            using (var aes = new AesGcm(_key, TagSize))
            {
                // Throws CryptographicException if the data was tampered with.
                aes.Decrypt(nonce, cipher, tag, plain);
            }

            return Encoding.UTF8.GetString(plain);
        }
    }
}
