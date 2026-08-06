using System.Security.Cryptography;

namespace RaceTrade.Web.Security;

/// <summary>
/// Controls who can reach the UI.
///
/// This app holds site logins, cbftp passwords and Blowfish keys, so the default is
/// deliberately closed: listen on loopback only, where the OS itself is the access
/// control. Opening it up is an explicit decision, and doing so REQUIRES a password -
/// see <see cref="Validate"/>, which refuses to start an unauthenticated listener on
/// a non-loopback address rather than silently exposing everything.
///
/// Even with a password set, prefer a tunnel (WireGuard/Tailscale/SSH -L) over
/// exposing the port directly: that keeps the app invisible to the internet and gives
/// you transport encryption without certificate juggling.
/// </summary>
public sealed class WebSecurityOptions
{
    public const string SectionName = "Web";

    /// <summary>Address to bind. Loopback by default; "0.0.0.0" exposes it on the LAN.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8420;

    /// <summary>PBKDF2 hash of the admin password. Empty = no login (loopback only).</summary>
    public string PasswordHash { get; set; } = "";

    /// <summary>Salt for <see cref="PasswordHash"/>, base64.</summary>
    public string PasswordSalt { get; set; } = "";

    /// <summary>Failed logins allowed per window before the client is throttled.</summary>
    public int MaxLoginAttempts { get; set; } = 5;

    public int LoginLockoutMinutes { get; set; } = 15;

    public bool IsLoopbackOnly =>
        BindAddress is "127.0.0.1" or "::1" or "localhost";

    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    /// <summary>
    /// Fails fast on an unsafe combination instead of starting an open, credential-
    /// holding web app on the network.
    /// </summary>
    public void Validate()
    {
        if (!IsLoopbackOnly && !HasPassword)
        {
            throw new InvalidOperationException(
                $"Refusing to start: BindAddress is '{BindAddress}' (reachable from other machines) " +
                "but no admin password is set. Either bind to 127.0.0.1, or set a password first " +
                "(run with --set-password). This app stores FTP and cbftp credentials.");
        }
    }

    private const int Iterations = 210_000; // OWASP-recommended floor for PBKDF2-SHA256
    private const int HashBytes = 32;
    private const int SaltBytes = 16;

    public static (string hash, string salt) HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool VerifyPassword(string password)
    {
        if (!HasPassword || string.IsNullOrEmpty(password))
            return false;

        var salt = Convert.FromBase64String(PasswordSalt);
        var expected = Convert.FromBase64String(PasswordHash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);

        // Constant time: a length/'==' comparison leaks how much of the hash matched.
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
