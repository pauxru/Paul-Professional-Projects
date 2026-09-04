using System.Text.RegularExpressions;

namespace AgentPlatform.Infrastructure.Tools;

/// <summary>
/// A deterministic, dependency-free extractive summariser. It scores each sentence by the frequency
/// of its (non-stopword) terms and returns the top-N sentences in their original order. Being pure
/// code, it produces identical output every run — the summarisation workflow uses it so results are
/// reproducible offline without a model.
/// </summary>
internal static class ExtractiveSummary
{
    private static readonly Regex SentenceSplit = new(@"(?<=[\.!\?])\s+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    private static readonly Regex WordSplit = new(@"[^a-z0-9]+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "and", "or", "but", "if", "then", "is", "are", "was", "were", "be", "been",
        "to", "of", "in", "on", "for", "with", "as", "at", "by", "from", "this", "that", "it", "its",
        "we", "you", "they", "i", "he", "she", "our", "your", "their", "will", "would", "can", "could",
        "have", "has", "had", "do", "does", "did", "not", "no", "so", "up", "out", "about", "into",
    };

    public static IReadOnlyList<string> Summarise(string text, int maxSentences)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();

        var sentences = SentenceSplit.Split(text.Trim())
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();

        if (sentences.Count <= maxSentences) return sentences;

        var frequencies = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in Words(text))
            frequencies[word] = frequencies.GetValueOrDefault(word) + 1;

        var scored = sentences
            .Select((sentence, index) => (sentence, index, score: Score(sentence, frequencies)))
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.index)
            .Take(maxSentences)
            .OrderBy(x => x.index)
            .Select(x => x.sentence)
            .ToList();

        return scored;
    }

    private static double Score(string sentence, IReadOnlyDictionary<string, int> frequencies)
    {
        var words = Words(sentence).ToList();
        if (words.Count == 0) return 0;
        return words.Sum(w => frequencies.GetValueOrDefault(w)) / (double)words.Count;
    }

    private static IEnumerable<string> Words(string text) =>
        WordSplit.Split(text.ToLowerInvariant())
            .Where(w => w.Length > 2 && !StopWords.Contains(w));
}
