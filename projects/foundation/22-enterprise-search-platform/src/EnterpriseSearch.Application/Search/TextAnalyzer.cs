using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public interface ITextAnalyzer
{
    IReadOnlyList<AnalyzedToken> Analyze(string text, AnalyzerDefinition definition);
}

public sealed class TextAnalyzer : ITextAnalyzer
{
    private static readonly Regex Html = new("<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Standard = new("[\\p{L}\\p{Nd}]+(?:['’-][\\p{L}\\p{Nd}]+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex WhiteSpace = new("\\S+", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "a", "an", "and", "are", "as", "at", "be", "by", "for", "from", "in", "is", "it", "of", "on", "or", "that", "the", "this", "to", "with"
    };
    private static readonly IReadOnlyDictionary<string, string[]> Synonyms = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["laptop"] = ["notebook"],
        ["notebook"] = ["laptop"],
        ["tv"] = ["television"],
        ["television"] = ["tv"],
        ["phone"] = ["smartphone"],
        ["smartphone"] = ["phone"],
        ["headphones"] = ["headset"],
        ["headset"] = ["headphones"]
    };

    public IReadOnlyList<AnalyzedToken> Analyze(string text, AnalyzerDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(definition);
        var prepared = ApplyCharacterFilters(text, definition);
        var raw = Tokenize(prepared, definition);
        var filtered = new List<AnalyzedToken>();
        var primary = new List<AnalyzedToken>();

        foreach (var token in raw)
        {
            var term = token.Term;
            if (definition.RemoveStopWords && StopWords.Contains(term))
            {
                continue;
            }

            if (definition.Stem && definition.Tokenizer is not TokenizerKind.NGram and not TokenizerKind.EdgeNGram)
            {
                term = PorterStemmer.Stem(term);
            }

            if (term.Length == 0)
            {
                continue;
            }

            var normalized = token with { Term = term };
            filtered.Add(normalized);
            primary.Add(normalized);
            if (definition.ExpandSynonyms && Synonyms.TryGetValue(term, out var expansions))
            {
                foreach (var synonym in expansions)
                {
                    filtered.Add(normalized with { Term = synonym, Type = "SYNONYM" });
                }
            }
        }

        if (definition.Shingles)
        {
            for (var i = 0; i + 1 < primary.Count; i++)
            {
                var first = primary[i];
                var second = primary[i + 1];
                if (second.Position - first.Position == 1)
                {
                    filtered.Add(new AnalyzedToken($"{first.Term}_{second.Term}", first.Position, first.StartOffset, second.EndOffset, "SHINGLE"));
                }
            }
        }

        return filtered.OrderBy(token => token.Position).ThenBy(token => token.Type == "SYNONYM" ? 1 : 0).ThenBy(token => token.Term, StringComparer.Ordinal).ToArray();
    }

    private static string ApplyCharacterFilters(string text, AnalyzerDefinition definition)
    {
        var value = definition.HtmlStrip ? Html.Replace(text, " ") : text;
        if (definition.UnicodeNormalize)
        {
            value = value.Normalize(NormalizationForm.FormKD);
        }

        if (definition.AccentFold)
        {
            var builder = new StringBuilder(value.Length);
            foreach (var character in value)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(character);
                }
            }
            value = builder.ToString().Normalize(NormalizationForm.FormKC);
        }

        return definition.Lowercase ? value.ToLowerInvariant() : value;
    }

    private static IReadOnlyList<AnalyzedToken> Tokenize(string text, AnalyzerDefinition definition)
    {
        var matches = definition.Tokenizer == TokenizerKind.Whitespace ? WhiteSpace.Matches(text) : Standard.Matches(text);
        var tokens = new List<AnalyzedToken>();
        var position = 0;
        foreach (Match match in matches)
        {
            if (definition.Tokenizer == TokenizerKind.NGram)
            {
                AddNGrams(tokens, match.Value, position, match.Index, definition.MinGram, definition.MaxGram, false);
            }
            else if (definition.Tokenizer == TokenizerKind.EdgeNGram)
            {
                AddNGrams(tokens, match.Value, position, match.Index, definition.MinGram, definition.MaxGram, true);
            }
            else
            {
                tokens.Add(new AnalyzedToken(match.Value, position, match.Index, match.Index + match.Length, "WORD"));
            }
            position++;
        }
        return tokens;
    }

    private static void AddNGrams(List<AnalyzedToken> target, string term, int position, int start, int minGram, int maxGram, bool edge)
    {
        var upper = Math.Min(Math.Max(minGram, maxGram), term.Length);
        for (var length = Math.Max(1, minGram); length <= upper; length++)
        {
            if (edge)
            {
                target.Add(new AnalyzedToken(term[..length], position, start, start + length, "EDGE_NGRAM"));
                continue;
            }

            for (var offset = 0; offset + length <= term.Length; offset++)
            {
                target.Add(new AnalyzedToken(term.Substring(offset, length), position, start + offset, start + offset + length, "NGRAM"));
            }
        }
    }
}
