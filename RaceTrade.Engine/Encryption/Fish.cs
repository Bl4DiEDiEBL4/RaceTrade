using System;
using System.Text;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;

public class FishDecryptor
{
    private static readonly string FishAlphabet = "./0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private readonly byte[] keyBytes;
    private readonly SecureRandom random = new SecureRandom();

    public FishDecryptor(string blowfishKey)
    {
        if (string.IsNullOrWhiteSpace(blowfishKey))
            throw new ArgumentException("Blowfish key cannot be null or empty.", nameof(blowfishKey));

        // FiSH uses key bytes as-is (ANSI/UTF-8), NOT Base64-decoded.
        // DH1080 key is the 43-char Base64 string itself.
        keyBytes = Encoding.ASCII.GetBytes(blowfishKey);

        if (keyBytes.Length < 4)
            throw new ArgumentException("Blowfish key must be at least 4 bytes.");

        // Blowfish's maximum key size is 56 bytes (448 bits). Standard FiSH /
        // Mircryption clients don't reject a longer key — they silently use only
        // the first 56 bytes. Match that so keys set channel-side (which we can't
        // change) still decrypt instead of failing to load.
        if (keyBytes.Length > 56)
        {
            var truncated = new byte[56];
            Array.Copy(keyBytes, truncated, 56);
            keyBytes = truncated;
        }
    }

    // ───────────────────────────────
    // Public API: Encrypt / Decrypt
    // ───────────────────────────────

    /// <summary>
    /// Encrypts plaintext using FiSH CBC mode and returns "+OK *<base64>" (mIRC compatible).
    /// </summary>
    public string EncryptMessage(string plaintext)
    {
        if (plaintext == null)
            plaintext = string.Empty;

        string base64 = EncryptCbcCore(plaintext);
        return "+OK *" + base64;
    }

    /// <summary>
    /// Decrypts a FiSH message:
    /// "+OK *Base64..." → CBC
    /// "+OK fishchars..." → old ECB
    /// "mcps ..." also supported.
    /// </summary>
    public string DecryptMessage(string encryptedMessage)
    {
        if (string.IsNullOrWhiteSpace(encryptedMessage))
            throw new ArgumentException("Encrypted message cannot be null or empty.", nameof(encryptedMessage));

        string msg = encryptedMessage.Trim();

        // Strip FiSH prefixes "+OK " or "mcps "
        if (msg.StartsWith("+OK "))
        {
            msg = msg.Substring(4);
        }
        else if (msg.StartsWith("mcps "))
        {
            msg = msg.Substring(5);
        }
        else
        {
            throw new ArgumentException("Invalid FiSH encrypted message format");
        }

        if (string.IsNullOrEmpty(msg))
            return string.Empty;

        bool useCbc = false;

        // FiSH heuristic: if message starts with '*', it's CBC
        if (msg[0] == '*')
        {
            useCbc = true;
            msg = msg.Substring(1);
        }

        // DEBUG OUTPUT
        System.Diagnostics.Debug.WriteLine($"[FiSH] Decrypt mode: {(useCbc ? "CBC" : "ECB")}");

        return useCbc ? DecryptCbcCore(msg) : DecryptEcbCore(msg);
    }

    // ───────────────────────────────
    // CBC (Mircryption compatible, blowfish_cbc.cpp)
    // ───────────────────────────────

    private string EncryptCbcCore(string plaintext)
    {
        byte[] plain = Encoding.UTF8.GetBytes(plaintext);
        int inLen = plain.Length;

        // Compute buffer length: pad to multiple of 8, then add 8 for the random "IV" block.
        int bufLen = inLen;
        if (bufLen % 8 != 0)
            bufLen += 8 - (bufLen % 8);
        bufLen += 8;

        byte[] bufIn = new byte[bufLen];

        // First 8 bytes = random prefix (FiSH/Mircryption pseudo-IV)
        byte[] realIv = new byte[8];
        random.NextBytes(realIv);
        Array.Copy(realIv, 0, bufIn, 0, 8);

        // Copy plaintext bytes starting at offset 8; trailing bytes remain zero-padded
        Array.Copy(plain, 0, bufIn, 8, plain.Length);

        // Blowfish-CBC with IV = all zeros, *no padding* (we already padded)
        var engine = new BlowfishEngine();
        var cbc = new CbcBlockCipher(engine);
        var cipher = new BufferedBlockCipher(cbc);
        byte[] zeroIv = new byte[8];
        cipher.Init(true, new ParametersWithIV(new KeyParameter(TrimKey(keyBytes)), zeroIv));

        byte[] outBuf = new byte[cipher.GetOutputSize(bufIn.Length)];
        int len = cipher.ProcessBytes(bufIn, 0, bufIn.Length, outBuf, 0);
        len += cipher.DoFinal(outBuf, len);

        return Convert.ToBase64String(outBuf, 0, len);
    }

    private string DecryptCbcCore(string base64Payload)
    {
        byte[] cipherData = Convert.FromBase64String(base64Payload);

        if (cipherData.Length == 0)
            throw new ArgumentException("Empty CBC ciphertext");

        // FiSH silently truncates to multiple of 8 bytes.
        int rem = cipherData.Length % 8;
        if (rem != 0)
        {
            int newLen = cipherData.Length - rem;
            if (newLen <= 0)
                throw new ArgumentException("Invalid CBC ciphertext length");
            byte[] tmp = new byte[newLen];
            Array.Copy(cipherData, tmp, newLen);
            cipherData = tmp;
        }

        var engine = new BlowfishEngine();
        var cbc = new CbcBlockCipher(engine);
        var cipher = new BufferedBlockCipher(cbc);
        byte[] zeroIv = new byte[8];
        cipher.Init(false, new ParametersWithIV(new KeyParameter(TrimKey(keyBytes)), zeroIv));

        byte[] decrypted = new byte[cipher.GetOutputSize(cipherData.Length)];
        int len = cipher.ProcessBytes(cipherData, 0, cipherData.Length, decrypted, 0);
        len += cipher.DoFinal(decrypted, len);

        if (len < 8)
            return string.Empty;

        int dataLen = len - 8; // strip the first 8 bytes (random prefix)
        byte[] plainPadded = new byte[dataLen];
        Array.Copy(decrypted, 8, plainPadded, 0, dataLen);

        string text = Encoding.UTF8.GetString(plainPadded);

        // FiSH removes nulls; also strip CR/LF just to be safe.
        return text.TrimEnd('\0', '\r', '\n');
    }

    // ───────────────────────────────
    // Old FiSH ECB (blowfish.cpp)
    // ───────────────────────────────

    private string DecryptEcbCore(string fishCipher)
    {
        // Input must be multiple of 12 and only use fish alphabet
        if (fishCipher.Length < 12)
            throw new ArgumentException("Invalid ECB ciphertext length");

        int cutOff = fishCipher.Length % 12;
        bool hasCut = cutOff > 0;
        if (hasCut)
        {
            fishCipher = fishCipher.Substring(0, fishCipher.Length - cutOff);
        }

        foreach (char c in fishCipher)
        {
            if (FishAlphabet.IndexOf(c) < 0)
                throw new ArgumentException("Invalid character in ECB ciphertext");
        }

        // Decode FiSH-base64 into raw Blowfish ciphertext bytes
        byte[] cipherBytes = FishDecodeBlocks(fishCipher);

        // Blowfish ECB, no padding (we manually padded)
        var engine = new BlowfishEngine();
        var cipher = new BufferedBlockCipher(engine);
        cipher.Init(false, new KeyParameter(TrimKey(keyBytes)));

        byte[] plainPadded = new byte[cipher.GetOutputSize(cipherBytes.Length)];
        int len = cipher.ProcessBytes(cipherBytes, 0, cipherBytes.Length, plainPadded, 0);
        len += cipher.DoFinal(plainPadded, len);

        // FiSH removes 0x00, 0x0d, 0x0a from result.
        var sb = new StringBuilder(len);
        for (int i = 0; i < len; i++)
        {
            byte b = plainPadded[i];
            if (b != 0x00 && b != 0x0d && b != 0x0a)
                sb.Append((char)b);
        }

        return sb.ToString();
    }

    private static byte[] FishDecodeBlocks(string fishCipher)
    {
        int blocks = fishCipher.Length / 12;
        byte[] result = new byte[blocks * 8];

        int pos = 0;
        for (int blk = 0; blk < blocks; blk++)
        {
            uint right = 0;
            uint left = 0;

            // First 6 chars → right
            for (int part = 0; part < 6; part++)
            {
                int idx = FishAlphabet.IndexOf(fishCipher[pos++]);
                if (idx < 0) throw new ArgumentException("Invalid FiSH base64 char");
                right |= (uint)idx << (part * 6);
            }

            // Next 6 chars → left
            for (int part = 0; part < 6; part++)
            {
                int idx = FishAlphabet.IndexOf(fishCipher[pos++]);
                if (idx < 0) throw new ArgumentException("Invalid FiSH base64 char");
                left |= (uint)idx << (part * 6);
            }

            int offset = blk * 8;
            // Big-endian: same as FiSH code
            result[offset + 0] = (byte)((left >> 24) & 0xFF);
            result[offset + 1] = (byte)((left >> 16) & 0xFF);
            result[offset + 2] = (byte)((left >> 8) & 0xFF);
            result[offset + 3] = (byte)(left & 0xFF);
            result[offset + 4] = (byte)((right >> 24) & 0xFF);
            result[offset + 5] = (byte)((right >> 16) & 0xFF);
            result[offset + 6] = (byte)((right >> 8) & 0xFF);
            result[offset + 7] = (byte)(right & 0xFF);
        }

        return result;
    }

    private static byte[] TrimKey(byte[] key)
    {
        if (key.Length <= 56) return key;
        byte[] trimmed = new byte[56];
        Array.Copy(key, trimmed, 56);
        return trimmed;
    }
}
