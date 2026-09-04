using System.Security.Cryptography;
using System.Text;

namespace RagAssistant.Application.Common;

public static class Hashing
{
    public static string ComputeContentHash(string content)
    {
        var normalized = TextTokenizer.NormalizeWhitespace(content ?? string.Empty);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputePromptHash(string body)
    {
        var normalized = (body ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
