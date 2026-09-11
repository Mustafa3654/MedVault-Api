using System.Security.Cryptography;

namespace MedVault.Api.Data;

/// <summary>
/// PBKDF2-SHA256. Format: pbkdf2$&lt;iterations&gt;$&lt;base64 salt&gt;$&lt;base64 hash&gt;.
///
/// Patient passwords must be stored in this form (or bcrypt/Argon2), never as plaintext in an HIS
/// column. If the HIS already holds plaintext passwords, hash them in place before go-live.
/// </summary>
public static class PasswordHasher
{
    private const int Iterations = 210_000;   // OWASP 2023 guidance for PBKDF2-SHA256
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <summary>
    /// A hash of a value nobody knows, for verifying against when no account was found. Without it,
    /// an unknown mobile number returns immediately while a known one spends 210,000 iterations, and
    /// that difference is measurable - which would hand back the account enumeration that the shared
    /// "Invalid credentials" message exists to prevent.
    /// </summary>
    public static readonly string DummyHash =
        Hash(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)));

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"pbkdf2${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public static bool Verify(string password, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return false;

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "pbkdf2") return false;
        if (!int.TryParse(parts[1], out var iterations)) return false;

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

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
