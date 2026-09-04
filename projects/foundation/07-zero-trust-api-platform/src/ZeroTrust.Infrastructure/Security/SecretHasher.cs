using System.Security.Cryptography;
using System.Text;

namespace ZeroTrust.Infrastructure.Security;

public static class SecretHasher
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Iterations = 100_000;

    public static (string hash, string salt) Hash(string secret)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), saltBytes, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hashBytes), Convert.ToBase64String(saltBytes));
    }

    public static bool Verify(string secret, string storedHash, string storedSalt)
    {
        var saltBytes = Convert.FromBase64String(storedSalt);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(secret), saltBytes, Iterations, HashAlgorithmName.SHA256, HashBytes);
        var expected = Convert.FromBase64String(storedHash);
        return CryptographicOperations.FixedTimeEquals(expected, hashBytes);
    }

    public static string Sha256Hex(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static string Sha256Hex(byte[] value)
    {
        var bytes = SHA256.HashData(value);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
