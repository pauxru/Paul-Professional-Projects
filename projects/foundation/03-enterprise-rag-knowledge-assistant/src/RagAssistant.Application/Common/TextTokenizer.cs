using System.Text;
using System.Text.RegularExpressions;

namespace RagAssistant.Application.Common;

public static partial class TextTokenizer
{
    private static readonly Regex WordRegex = WordPattern();

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "from",
        "has", "have", "how", "i", "in", "is", "it", "its", "of", "on", "or",
        "our", "so", "than", "that", "the", "their", "them", "then", "there",
        "these", "they", "this", "to", "was", "we", "were", "what", "when",
        "where", "which", "who", "why", "will", "with", "you", "your",
    };

    public static IEnumerable<string> Tokenize(string text, bool removeStopWords = true)
    {
        if (string.IsNullOrEmpty(text))
        {
            yield break;
        }

        foreach (Match match in WordRegex.Matches(text))
        {
            var token = match.Value.ToLowerInvariant();
            if (token.Length == 0)
            {
                continue;
            }

            if (removeStopWords && StopWords.Contains(token))
            {
                continue;
            }

            yield return token;
        }
    }

    public static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var previousWhitespace = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!previousWhitespace)
                {
                    sb.Append(' ');
                    previousWhitespace = true;
                }
            }
            else
            {
                sb.Append(c);
                previousWhitespace = false;
            }
        }

        return sb.ToString().Trim();
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.CultureInvariant | RegexOptions.Compiled)]
    private static partial Regex WordPattern();
}
