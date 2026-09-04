using System.Diagnostics;
using EnterpriseSearch.Domain.Search;

namespace EnterpriseSearch.Application.Search;

public interface IVectorIndex
{
    string Name { get; }
    void Upsert(string documentId, IReadOnlyList<double> vector);
    void Delete(string documentId);
    IReadOnlyList<VectorHit> Search(IReadOnlyList<double> query, int size, CancellationToken cancellationToken = default);
    int Count { get; }
    int LastCandidateCount { get; }
}

public interface IEmbeddingModel
{
    IReadOnlyList<double> Embed(string text);
}

public sealed class DeterministicEmbeddingModel(ITextAnalyzer analyzer, int dimensions = 96) : IEmbeddingModel
{
    public IReadOnlyList<double> Embed(string text)
    {
        var vector = new double[dimensions];
        foreach (var token in analyzer.Analyze(text, new AnalyzerDefinition("embedding", RemoveStopWords: true, Stem: true)))
        {
            if (token.Type is not ("WORD" or null)) continue;
            var hash = StableHash(token.Term);
            var slot = (int)(hash % (uint)dimensions);
            var sign = (hash & 0x80000000) == 0 ? 1d : -1d;
            vector[slot] += sign;
        }
        Normalize(vector);
        return vector;
    }

    internal static void Normalize(double[] vector)
    {
        var magnitude = Math.Sqrt(vector.Sum(value => value * value));
        if (magnitude == 0d) return;
        for (var i = 0; i < vector.Length; i++) vector[i] /= magnitude;
    }

    internal static double Cosine(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var length = Math.Min(left.Count, right.Count);
        var dot = 0d;
        for (var i = 0; i < length; i++) dot += left[i] * right[i];
        return dot;
    }

    private static uint StableHash(string value)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        var hash = offset;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= prime;
        }
        return hash;
    }
}

public sealed class ExactVectorIndex : IVectorIndex
{
    private readonly object _gate = new();
    private readonly Dictionary<string, double[]> _vectors = new(StringComparer.Ordinal);
    private int _lastCandidateCount;
    public string Name => "exact-cosine";
    public int Count { get { lock (_gate) return _vectors.Count; } }
    public int LastCandidateCount { get { lock (_gate) return _lastCandidateCount; } }

    public void Upsert(string documentId, IReadOnlyList<double> vector)
    {
        lock (_gate) _vectors[documentId] = vector.ToArray();
    }

    public void Delete(string documentId)
    {
        lock (_gate) _vectors.Remove(documentId);
    }

    public IReadOnlyList<VectorHit> Search(IReadOnlyList<double> query, int size, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _lastCandidateCount = _vectors.Count;
            return _vectors.Select(pair =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new VectorHit(pair.Key, DeterministicEmbeddingModel.Cosine(query, pair.Value));
                })
                .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.DocumentId, StringComparer.Ordinal).Take(size).ToArray();
        }
    }
}

public sealed class ClusteredVectorIndex : IVectorIndex
{
    private sealed class Cluster(double[] centroid)
    {
        public double[] Centroid { get; } = centroid;
        public List<KeyValuePair<string, double[]>> Members { get; } = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, double[]> _vectors = new(StringComparer.Ordinal);
    private readonly int _maximumClusters;
    private readonly int _probeCount;
    private List<Cluster> _clusters = [];
    private bool _dirty = true;
    private int _lastCandidateCount;

    public ClusteredVectorIndex(int maximumClusters = 12, int probeCount = 3)
    {
        _maximumClusters = Math.Max(1, maximumClusters);
        _probeCount = Math.Max(1, probeCount);
    }

    public string Name => "ivf-clustered";
    public int Count { get { lock (_gate) return _vectors.Count; } }
    public int LastCandidateCount { get { lock (_gate) return _lastCandidateCount; } }

    public void Upsert(string documentId, IReadOnlyList<double> vector)
    {
        lock (_gate)
        {
            _vectors[documentId] = vector.ToArray();
            _dirty = true;
        }
    }

    public void Delete(string documentId)
    {
        lock (_gate)
        {
            _vectors.Remove(documentId);
            _dirty = true;
        }
    }

    public IReadOnlyList<VectorHit> Search(IReadOnlyList<double> query, int size, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            EnsureBuilt();
            var members = _clusters
                .Select(cluster => new { Cluster = cluster, Similarity = DeterministicEmbeddingModel.Cosine(query, cluster.Centroid) })
                .OrderByDescending(candidate => candidate.Similarity)
                .Take(_probeCount)
                .SelectMany(candidate => candidate.Cluster.Members)
                .ToArray();
            _lastCandidateCount = members.Length;
            return members.Select(pair =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return new VectorHit(pair.Key, DeterministicEmbeddingModel.Cosine(query, pair.Value));
                })
                .OrderByDescending(hit => hit.Score).ThenBy(hit => hit.DocumentId, StringComparer.Ordinal).Take(size).ToArray();
        }
    }

    private void EnsureBuilt()
    {
        if (!_dirty) return;
        var ordered = _vectors.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray();
        if (ordered.Length == 0)
        {
            _clusters = [];
            _dirty = false;
            return;
        }

        var count = Math.Min(_maximumClusters, ordered.Length);
        _clusters = Enumerable.Range(0, count)
            .Select(index => new Cluster(ordered[index * ordered.Length / count].Value.ToArray()))
            .ToList();
        for (var iteration = 0; iteration < 4; iteration++)
        {
            foreach (var cluster in _clusters) cluster.Members.Clear();
            foreach (var vector in ordered)
            {
                var chosen = _clusters.MaxBy(cluster => DeterministicEmbeddingModel.Cosine(vector.Value, cluster.Centroid))!;
                chosen.Members.Add(vector);
            }
            foreach (var cluster in _clusters.Where(cluster => cluster.Members.Count > 0))
            {
                Array.Clear(cluster.Centroid);
                foreach (var member in cluster.Members)
                {
                    for (var dimension = 0; dimension < cluster.Centroid.Length; dimension++) cluster.Centroid[dimension] += member.Value[dimension];
                }
                DeterministicEmbeddingModel.Normalize(cluster.Centroid);
            }
        }
        _dirty = false;
    }
}

public static class VectorSearchMeasurement
{
    public static VectorComparison Compare(IVectorIndex exact, IVectorIndex approximate, IReadOnlyList<double> query, int k, CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var exactHits = exact.Search(query, k, cancellationToken);
        stopwatch.Stop();
        var exactLatency = stopwatch.Elapsed;
        stopwatch.Restart();
        var approximateHits = approximate.Search(query, k, cancellationToken);
        stopwatch.Stop();
        var shared = exactHits.Select(hit => hit.DocumentId).Intersect(approximateHits.Select(hit => hit.DocumentId), StringComparer.Ordinal).Count();
        return new VectorComparison(k == 0 ? 1d : (double)shared / k, exactLatency, stopwatch.Elapsed, exact.LastCandidateCount, approximate.LastCandidateCount);
    }
}
