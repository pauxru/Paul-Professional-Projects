using RagAssistant.Application.Common;

namespace RagAssistant.Application.Retrieval;

public sealed record Bm25Options(double K1 = 1.5, double B = 0.75);

public sealed record Bm25Document(string Id, string Text);

public sealed record Bm25Hit(string Id, double Score);

public sealed class Bm25Index
{
    private readonly Bm25Options _options;
    private readonly Dictionary<string, Dictionary<string, int>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _docLengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _docFrequency = new(StringComparer.Ordinal);
    private double _avgDocLength;
    private int _documentCount;

    public Bm25Index(Bm25Options? options = null)
    {
        _options = options ?? new Bm25Options();
    }

    public int DocumentCount => _documentCount;
    public double AverageDocumentLength => _avgDocLength;

    public void Add(IEnumerable<Bm25Document> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        foreach (var doc in documents)
        {
            AddInternal(doc);
        }

        RecomputeAverageLength();
    }

    public void Add(Bm25Document document)
    {
        AddInternal(document);
        RecomputeAverageLength();
    }

    private void AddInternal(Bm25Document document)
    {
        var tokens = TextTokenizer.Tokenize(document.Text, removeStopWords: false).ToArray();
        _docLengths[document.Id] = tokens.Length;

        var freq = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var t in tokens)
        {
            freq[t] = freq.TryGetValue(t, out var c) ? c + 1 : 1;
        }

        foreach (var (term, count) in freq)
        {
            if (!_postings.TryGetValue(term, out var postings))
            {
                postings = new Dictionary<string, int>(StringComparer.Ordinal);
                _postings[term] = postings;
            }

            postings[document.Id] = count;
            _docFrequency[term] = _docFrequency.TryGetValue(term, out var df) ? df + 1 : 1;
        }

        _documentCount += 1;
    }

    private void RecomputeAverageLength()
    {
        if (_docLengths.Count == 0)
        {
            _avgDocLength = 0;
            return;
        }

        _avgDocLength = _docLengths.Values.Average();
    }

    public IReadOnlyList<Bm25Hit> Search(string query, int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || _documentCount == 0)
        {
            return Array.Empty<Bm25Hit>();
        }

        var queryTokens = TextTokenizer.Tokenize(query, removeStopWords: false).ToArray();
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var term in queryTokens)
        {
            if (!_postings.TryGetValue(term, out var postings))
            {
                continue;
            }

            var df = _docFrequency[term];
            var idf = Math.Log(1 + (_documentCount - df + 0.5) / (df + 0.5));
            foreach (var (docId, tf) in postings)
            {
                var length = _docLengths[docId];
                var denominator = tf + _options.K1 * (1 - _options.B + _options.B * (length / Math.Max(1e-6, _avgDocLength)));
                var termScore = idf * ((tf * (_options.K1 + 1)) / denominator);
                scores[docId] = scores.TryGetValue(docId, out var s) ? s + termScore : termScore;
            }
        }

        return scores
            .OrderByDescending(kv => kv.Value)
            .Take(limit)
            .Select(kv => new Bm25Hit(kv.Key, kv.Value))
            .ToArray();
    }
}
