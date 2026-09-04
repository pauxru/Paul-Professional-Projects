using System.Security.Cryptography;
using System.Text;

namespace Contoso.Payments.Application.Common;

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes, hash);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string Sha256Hex(string input) => Sha256Hex(Encoding.UTF8.GetBytes(input));
}
