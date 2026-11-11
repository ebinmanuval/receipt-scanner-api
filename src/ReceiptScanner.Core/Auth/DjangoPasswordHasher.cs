using System.Security.Cryptography;
using System.Text;

namespace ReceiptScanner.Core.Auth;

/// <summary>
/// Django's default password hasher, <c>pbkdf2_sha256</c>, so users created by
/// the Django app log in unchanged and users created here work in Django.
/// </summary>
/// <remarks>
/// Stored format: <c>pbkdf2_sha256$&lt;iterations&gt;$&lt;salt&gt;$&lt;base64 hash&gt;</c>,
/// where the hash is PBKDF2-HMAC-SHA256 over UTF-8 password and salt, with a
/// 32-byte output. Hashes in other formats (a password set to unusable, which
/// starts with "!", or another hasher) never verify.
/// </remarks>
public static class DjangoPasswordHasher
{
    public const string Algorithm = "pbkdf2_sha256";

    /// <summary>Django 5.1's default. New hashes use this; old hashes keep their own count.</summary>
    public const int DefaultIterations = 870_000;

    private const int HashBytes = 32;
    private const string SaltAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const int SaltLength = 22;   // Django: 128 bits of entropy from a 62-char alphabet

    /// <summary>Hash for a password Django would also accept.</summary>
    public static string Hash(string password, int iterations = DefaultIterations, string? salt = null)
    {
        ArgumentNullException.ThrowIfNull(password);
        salt ??= RandomNumberGenerator.GetString(SaltAlphabet, SaltLength);
        if (salt.Contains('$')) throw new ArgumentException("Salt can't contain '$'.", nameof(salt));

        var hash = Derive(password, salt, iterations);
        return $"{Algorithm}${iterations}${salt}${Convert.ToBase64String(hash)}";
    }

    /// <summary>True when <paramref name="password"/> matches <paramref name="encoded"/>. Constant-time compare.</summary>
    public static bool Verify(string password, string? encoded)
    {
        if (password is null || !TryParse(encoded, out var iterations, out var salt, out var expected))
            return false;

        var actual = Derive(password, salt, iterations);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>
    /// Burn the same time as a real check. Called when the username doesn't
    /// exist, so response time doesn't reveal which usernames do (Django does
    /// the same in ModelBackend.authenticate).
    /// </summary>
    public static void SimulateVerify(string password) =>
        Derive(password ?? "", "dummysaltdummysalt0000", DefaultIterations);

    /// <summary>True when a stored hash uses fewer iterations than today's default (Django's must_update).</summary>
    public static bool NeedsRehash(string? encoded) =>
        TryParse(encoded, out var iterations, out _, out _) && iterations < DefaultIterations;

    private static byte[] Derive(string password, string salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            Encoding.UTF8.GetBytes(salt),
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);

    private static bool TryParse(string? encoded, out int iterations, out string salt, out byte[] hash)
    {
        iterations = 0;
        salt = "";
        hash = [];

        var parts = (encoded ?? "").Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm) return false;
        if (!int.TryParse(parts[1], System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out iterations) || iterations <= 0)
            return false;

        salt = parts[2];
        try
        {
            hash = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }
        return hash.Length == HashBytes;
    }
}
