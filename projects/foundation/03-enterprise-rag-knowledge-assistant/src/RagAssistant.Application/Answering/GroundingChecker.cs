using RagAssistant.Application.Common;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.Application.Answering;

public sealed record GroundingSpan(int SentenceIndex, string Sentence, Guid ChunkId, double SupportScore);

public sealed record GroundingResult(
    IReadOnlyList<GroundingSpan> SupportedSentences,
    IReadOnlyList<string> UnsupportedSentences,
    double SupportRatio);

public interface IGroundingChecker
{
    GroundingResult Verify(string answer, IReadOnlyList<RetrievedChunk> chunks, double minSupport = 0.3);
}

public sealed class LexicalGroundingChecker : IGroundingChecker
{
    public GroundingResult Verify(string answer, IReadOnlyList<RetrievedChunk> chunks, double minSupport = 0.3)
    {
        if (chunks is null || chunks.Count == 0 || string.IsNullOrWhiteSpace(answer))
        {
            return new GroundingResult(Array.Empty<GroundingSpan>(), Array.Empty<string>(), 0);
        }

        var sentences = SplitSentences(answer);
        if (sentences.Count == 0)
        {
            return new GroundingResult(Array.Empty<GroundingSpan>(), Array.Empty<string>(), 0);
        }

        var chunkTokens = chunks.ToDictionary(
            c => c.ChunkId,
            c => new HashSet<string>(TextTokenizer.Tokenize(c.Content), StringComparer.Ordinal));

        var supported = new List<GroundingSpan>();
        var unsupported = new List<string>();

        for (var i = 0; i < sentences.Count; i++)
        {
            var sentence = sentences[i];
            var tokens = new HashSet<string>(TextTokenizer.Tokenize(sentence), StringComparer.Ordinal);
            if (tokens.Count == 0)
            {
                continue;
            }

            var (bestChunk, bestScore) = FindBestChunk(tokens, chunkTokens);
            if (bestScore >= minSupport)
            {
                supported.Add(new GroundingSpan(i, sentence, bestChunk, bestScore));
            }
            else
            {
                unsupported.Add(sentence);
            }
        }

        var total = Math.Max(1, supported.Count + unsupported.Count);
        var ratio = (double)supported.Count / total;
        return new GroundingResult(supported, unsupported, ratio);
    }

    private static (Guid ChunkId, double Score) FindBestChunk(
        HashSet<string> sentenceTokens,
        Dictionary<Guid, HashSet<string>> chunkTokens)
    {
        var bestScore = 0.0;
        var bestChunk = Guid.Empty;
        foreach (var (id, tokens) in chunkTokens)
        {
            if (tokens.Count == 0)
            {
                continue;
            }

            var overlap = sentenceTokens.Intersect(tokens).Count();
            var score = (double)overlap / sentenceTokens.Count;
            if (score > bestScore)
            {
                bestScore = score;
                bestChunk = id;
            }
        }

        return (bestChunk, bestScore);
    }

    internal static IReadOnlyList<string> SplitSentences(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '.' || c == '!' || c == '?' || c == '\n')
            {
                var end = i + 1;
                var slice = text[start..end].Trim();
                if (slice.Length > 0)
                {
                    results.Add(slice);
                }

                start = end;
            }
        }

        if (start < text.Length)
        {
            var tail = text[start..].Trim();
            if (tail.Length > 0)
            {
                results.Add(tail);
            }
        }

        return results;
    }
}
