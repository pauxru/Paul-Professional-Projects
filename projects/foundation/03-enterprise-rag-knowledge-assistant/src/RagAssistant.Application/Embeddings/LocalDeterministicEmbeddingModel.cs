using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Common;

namespace RagAssistant.Application.Embeddings;

public sealed class LocalDeterministicEmbeddingModel : IEmbeddingModel
{
    public const string DefaultModelId = "local-hashed-tfidf-v1";

    private readonly int _dimensions;

    public LocalDeterministicEmbeddingModel(int dimensions = 384)
    {
        if (dimensions < 32 || dimensions > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(dimensions), "Dimensions must be in [32, 4096].");
        }

        _dimensions = dimensions;
    }

    public string ModelId => DefaultModelId;
    public int Dimensions => _dimensions;

    public Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        return Task.FromResult(Embed(text));
    }

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var result = new float[texts.Count][];
        for (var i = 0; i < texts.Count; i++)
        {
            result[i] = Embed(texts[i]);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(result);
    }

    public float[] Embed(string text)
    {
        var vector = new float[_dimensions];
        if (string.IsNullOrEmpty(text))
        {
            return vector;
        }

        var tokens = TextTokenizer.Tokenize(text, removeStopWords: true).ToArray();
        if (tokens.Length == 0)
        {
            tokens = TextTokenizer.Tokenize(text, removeStopWords: false).ToArray();
        }

        if (tokens.Length == 0)
        {
            return vector;
        }

        var termCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens)
        {
            termCounts[t] = termCounts.TryGetValue(t, out var c) ? c + 1 : 1;
        }

        foreach (var (term, count) in termCounts)
        {
            var tf = 1.0 + Math.Log(count);
            var idf = ApproxIdf(term);
            var weight = (float)(tf * idf);

            var (index, sign) = HashToIndex(term, _dimensions);
            vector[index] += sign * weight;

            for (var offset = 1; offset <= 2; offset++)
            {
                var salt = term + "#" + offset;
                var (i2, s2) = HashToIndex(salt, _dimensions);
                vector[i2] += s2 * weight * 0.4f;
            }
        }

        AddCharacterNgrams(text, vector);

        Normalize(vector);
        return vector;
    }

    private static double ApproxIdf(string token)
    {
        var length = Math.Max(1, token.Length);
        return 1.0 + Math.Log(1.0 + length);
    }

    private static (int Index, int Sign) HashToIndex(string token, int dimensions)
    {
        unchecked
        {
            uint hash = 2166136261;
            foreach (var c in token)
            {
                hash ^= c;
                hash *= 16777619;
            }

            var sign = (hash & 1) == 0 ? 1 : -1;
            var index = (int)((hash >> 1) % (uint)dimensions);
            return (index, sign);
        }
    }

    private static void AddCharacterNgrams(string text, float[] vector)
    {
        var clean = TextTokenizer.NormalizeWhitespace(text).ToLowerInvariant();
        if (clean.Length < 3)
        {
            return;
        }

        for (var i = 0; i <= clean.Length - 3; i++)
        {
            var ngram = clean.Substring(i, 3);
            if (ngram.Contains(' ', StringComparison.Ordinal))
            {
                continue;
            }

            var (index, sign) = HashToIndex("ng3:" + ngram, vector.Length);
            vector[index] += sign * 0.25f;
        }
    }

    private static void Normalize(float[] vector)
    {
        double sumSquares = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sumSquares += vector[i] * vector[i];
        }

        if (sumSquares <= 0)
        {
            return;
        }

        var norm = Math.Sqrt(sumSquares);
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(vector[i] / norm);
        }
    }
}
