using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClubShell.Server.Auth;

/// <summary>
/// Player passwords, unlock PINs and the agent's <c>offlineHash</c> (DESIGN §3.4): <c>pbkdf2$210000$&lt;salt b64&gt;$&lt;hash b64&gt;</c>,
/// PBKDF2-SHA256, 16-byte salt, 32-byte hash — byte for byte the agent's <c>OfflineSessionStore.HashPassword</c>/<c>VerifyPassword</c>,
/// so an <c>offlineHash</c> issued here verifies on the PC.
/// </summary>
public static class Passwords
{
    public const int Iterations = 210_000;

    /// <summary>Verified when the user does not exist, so an unknown username costs the same time as a wrong password.</summary>
    private static readonly Lazy<string> Dummy = new(() => Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))));

    public static string Hash(string secret)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, Iterations, HashAlgorithmName.SHA256, 32);
        return string.Create(CultureInfo.InvariantCulture, $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}");
    }

    /// <summary>Constant-time check; a null or malformed <paramref name="encoded"/> fails (after a dummy derivation).</summary>
    public static bool Verify(string? encoded, string secret)
    {
        var parts = (encoded ?? Dummy.Value).Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2"
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var iterations) || iterations is < 1 or > 10_000_000)
        {
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (salt.Length == 0 || expected.Length == 0)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected) && encoded is not null;
    }
}
