using System.Security.Cryptography;

namespace GrowSandbox.Server.Security;

/// <summary>
/// Utilitas hashing password aman berbasis PBKDF2-HMAC-SHA256 (<c>System.Security.Cryptography</c>)
/// dengan 16-byte random salt, 100.000 iterasi, hash 32 byte, dan perbandingan konstan
/// <see cref="CryptographicOperations.FixedTimeEquals"/> untuk mencegah timing attack.
/// </summary>
public static class PasswordHasher
{
    public const int SaltSizeBytes = 16;
    public const int HashSizeBytes = 32;
    public const int Iterations = 100_000;

    public static (byte[] Hash, byte[] Salt) HashPassword(string password)
    {
        if (string.IsNullOrEmpty(password))
        {
            throw new ArgumentException("Password tidak boleh kosong.", nameof(password));
        }

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSizeBytes);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        return (hash, salt);
    }

    public static bool VerifyPassword(
        string password,
        ReadOnlySpan<byte> expectedHash,
        ReadOnlySpan<byte> salt)
    {
        if (string.IsNullOrEmpty(password) ||
            expectedHash.Length != HashSizeBytes ||
            salt.Length != SaltSizeBytes)
        {
            return false;
        }

        byte[] computed = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        try
        {
            return CryptographicOperations.FixedTimeEquals(computed, expectedHash);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(computed);
        }
    }
}
