using System;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

public class DH1080
{
    // From dh1080.cpp
    private const string DH1080_PRIME_STRING =
        "++ECLiPSE+is+proud+to+present+latest+FiSH+release+featuring+even+more+security+for+you+++shouts+go+out+to+TMG+for+helping+to+generate+this+cool+sophie+germain+prime+number++++/C32L";

    private static readonly BigInteger Prime;
    private static readonly int PrimeByteLength;
    private static readonly BigInteger Generator = new BigInteger(2);

    private readonly BigInteger privateKey;
    private readonly BigInteger publicKey;

    static DH1080()
    {
        // Decode prime using FiSH's custom Base64
        byte[] primeBytes = DH1080Base64Decode(DH1080_PRIME_STRING);
        if (primeBytes == null || primeBytes.Length == 0)
            throw new Exception("Failed to decode DH1080 prime.");

        PrimeByteLength = primeBytes.Length;
        Prime = BigEndianToBigInteger(primeBytes);
    }

    public DH1080()
    {
        using (var rng = RandomNumberGenerator.Create())
        {
            BigInteger x;
            do
            {
                byte[] buf = new byte[PrimeByteLength];
                rng.GetBytes(buf);
                x = BigEndianToBigInteger(buf);
            }
            while (x < 2 || x > Prime - 2);

            privateKey = x;
        }

        publicKey = BigInteger.ModPow(Generator, privateKey, Prime);
    }

    /// <summary>
    /// Our DH1080 public key string (same format as FiSH: custom Base64 over 135 bytes).
    /// </summary>
    public string GetPublicKey()
    {
        byte[] yBytes = BigIntegerToFixedBigEndian(publicKey, PrimeByteLength);
        // USE STANDARD BASE64 for mIRC compatibility
        return Convert.ToBase64String(yBytes);
    }

    /// <summary>
    /// Decodes a peer's DH1080 public key, accepting either standard Base64 (what this
    /// app emits) or the FiSH custom Base64 that real mIRC/FiSH clients emit. Whichever
    /// yields the expected key length wins.
    /// </summary>
    private static byte[] DecodeDh1080PublicKey(string key)
    {
        byte[] standard = null;
        byte[] fish = null;

        try { standard = Convert.FromBase64String(key); } catch { /* not standard base64 */ }
        if (standard != null && standard.Length == PrimeByteLength)
            return standard;

        try { fish = DH1080Base64Decode(key); } catch { /* not FiSH base64 */ }
        if (fish != null && fish.Length == PrimeByteLength)
            return fish;

        // Neither produced a valid length - return whatever we got so the caller's
        // length check reports a clear error.
        return standard ?? fish;
    }

    /// <summary>
    /// Computes the shared FiSH key string for the peer's DH1080 public key.
    /// Return value is the *FiSH Blowfish key string*, same as DH1080_Compute().
    /// </summary>
    public string ComputeSharedSecret(string otherPublicKey)
    {
        if (string.IsNullOrWhiteSpace(otherPublicKey))
            throw new ArgumentException("Other public key must not be null or empty.", nameof(otherPublicKey));

        // A FiSH custom-Base64 key can often ALSO parse as standard Base64 (the alphabets
        // overlap) but yields the wrong bytes/length. So don't just take the first that
        // parses - take whichever actually produces a valid 135-byte key.
        byte[] otherBytes = DecodeDh1080PublicKey(otherPublicKey.Trim());

        if (otherBytes == null || otherBytes.Length == 0)
            throw new ArgumentException("Invalid DH1080 public key (decode failed).", nameof(otherPublicKey));

        // Expect 135 bytes
        if (otherBytes.Length != PrimeByteLength)
            throw new ArgumentException($"Invalid DH1080 public key length (got {otherBytes.Length}, expected {PrimeByteLength}).");

        BigInteger otherPub = BigEndianToBigInteger(otherBytes);

        // Reject degenerate public keys (0, 1, p-1, >=p) — they force an
        // attacker-predictable shared secret (small-subgroup confinement).
        if (otherPub <= BigInteger.One || otherPub >= Prime - BigInteger.One)
            throw new ArgumentException("Invalid DH1080 public key (degenerate value).", nameof(otherPublicKey));

        // sharedSecret = otherPub ^ privateKey mod Prime
        BigInteger shared = BigInteger.ModPow(otherPub, privateKey, Prime);

        // Convert to big-endian and left-pad to PrimeByteLength (135 bytes)
        byte[] sharedBytes = BigIntegerToFixedBigEndian(shared, PrimeByteLength);

        // SHA256 over the padded secret
        byte[] hash;
        using (var sha = SHA256.Create())
        {
            hash = sha.ComputeHash(sharedBytes);
        }

        // RETURN STANDARD BASE64 (for FishDecryptor compatibility)
        // FishDecryptor expects the 43-char string as-is (ASCII bytes)
        string keyString = Convert.ToBase64String(hash).TrimEnd('=');

        return keyString;
    }

    // ───────────────────────────────
    // FiSH custom Base64 (DH1080_Base64_Encode / Decode)
    // ───────────────────────────────

    private static string DH1080Base64Encode(byte[] data)
    {
        if (data == null || data.Length == 0)
            return string.Empty;

        string b64 = Convert.ToBase64String(data); // standard Base64, includes '=' padding

        if (!b64.Contains("="))
        {
            // No padding, append sentinel 'A'
            return b64 + "A";
        }

        // Remove all '=' chars
        var sb = new StringBuilder(b64.Length);
        foreach (char c in b64)
        {
            if (c != '=')
                sb.Append(c);
        }
        return sb.ToString();
    }

    private static byte[] DH1080Base64Decode(string s)
    {
        if (string.IsNullOrEmpty(s))
            return Array.Empty<byte>();

        // If length % 4 == 1 and ends with 'A', remove 'A'
        if (s.Length % 4 == 1 && s[s.Length - 1] == 'A')
        {
            s = s.Substring(0, s.Length - 1);
        }

        // Pad with '=' to multiple of 4
        while (s.Length % 4 != 0)
        {
            s += "=";
        }

        return Convert.FromBase64String(s);
    }

    // ───────────────────────────────
    // BigInteger helpers
    // ───────────────────────────────

    private static BigInteger BigEndianToBigInteger(byte[] data)
    {
        if (data == null || data.Length == 0)
            return BigInteger.Zero;

        // Add a zero byte at end to force positive
        byte[] temp = new byte[data.Length + 1];
        for (int i = 0; i < data.Length; i++)
            temp[i] = data[data.Length - 1 - i]; // reverse to little-endian
        temp[temp.Length - 1] = 0; // positive sign

        return new BigInteger(temp);
    }

    private static byte[] BigIntegerToFixedBigEndian(BigInteger value, int size)
    {
        if (value < 0)
            throw new ArgumentOutOfRangeException(nameof(value), "Value must be non-negative.");

        byte[] little = value.ToByteArray(); // little-endian, two's complement
        // Strip leading sign bytes
        int len = little.Length;
        while (len > 1 && little[len - 1] == 0x00)
            len--;

        if (len > size)
            throw new ArgumentException($"BigInteger is too large to fit in {size} bytes.", nameof(value));

        byte[] result = new byte[size];
        // Copy to the end (big-endian)
        for (int i = 0; i < len; i++)
        {
            result[size - 1 - i] = little[i];
        }

        return result;
    }
}
