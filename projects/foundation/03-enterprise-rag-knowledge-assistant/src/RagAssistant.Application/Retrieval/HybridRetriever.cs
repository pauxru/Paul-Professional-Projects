using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Common;
using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.Application.Retrieval;

public enum RetrievalMode
{
    Keyword = 0,
    Dense = 1,
    Hybrid = 2,
}

public sealed record RetrievalRequest(
    string Query,
    UserPrincipal User,
    RetrievalMode Mode,
    int TopK = 6,
    int CandidateK = 24);

public sealed record RetrievalResult(
    IReadOnlyList<RetrievedChunk> Chunks,
    RetrievalMode Mode,
    int Candidates);

public interface IRetriever
{
    Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct);
}

public sealed class HybridRetriever : IRetriever
{
    private readonly IVectorStore _store;
    private readonly IEmbeddingModel _embedding;

    public HybridRetriever(IVectorStore store, IEmbeddingModel embedding)
    {
        _store = store;
        _embedding = embedding;
    }

    public async Task<RetrievalResult> RetrieveAsync(RetrievalRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TopK <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.TopK));
        }

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            return new RetrievalResult(Array.Empty<RetrievedChunk>(), request.Mode, 0);
        }

        var candidateK = Math.Max(request.CandidateK, request.TopK);

        var keywordTask = _store.KeywordSearchAsync(
            new KeywordSearchQuery(request.Query, candidateK, request.User), ct);

        var embedding = await _embedding.EmbedAsync(request.Query, ct).ConfigureAwait(false);
        var denseTask = _store.SearchAsync(
            new VectorSearchQuery(embedding, _embedding.ModelId, candidateK, request.User), ct);

        var keywordHits = await keywordTask.ConfigureAwait(false);
        var denseHits = await denseTask.ConfigureAwait(false);

        var byId = new Dictionary<Guid, RetrievedChunk>();
        foreach (var hit in keywordHits.Concat(denseHits))
        {
            byId.TryAdd(hit.ChunkId, hit);
        }

        var ordered = request.Mode switch
        {
            RetrievalMode.Keyword => TakeByScore(keywordHits, request.TopK),
            RetrievalMode.Dense => TakeByScore(denseHits, request.TopK),
            RetrievalMode.Hybrid => FuseAndRerank(request.Query, keywordHits, denseHits, byId, request.TopK),
            _ => throw new ArgumentOutOfRangeException(nameof(request.Mode)),
        };

        var filtered = ordered
            .Where(c => request.User.MaxClassification >= c.Classification)
            .ToArray();

        return new RetrievalResult(filtered, request.Mode, byId.Count);
    }

    private static IReadOnlyList<RetrievedChunk> TakeByScore(IReadOnlyList<RetrievedChunk> hits, int topK)
    {
        return hits
            .OrderByDescending(h => h.Score)
            .Take(topK)
            .ToArray();
    }

    private static IReadOnlyList<RetrievedChunk> FuseAndRerank(
        string query,
        IReadOnlyList<RetrievedChunk> keyword,
        IReadOnlyList<RetrievedChunk> dense,
        IReadOnlyDictionary<Guid, RetrievedChunk> byId,
        int topK)
    {
        var keywordRanking = keyword.Select(c => c.ChunkId).ToArray();
        var denseRanking = dense.Select(c => c.ChunkId).ToArray();
        var fused = ReciprocalRankFusion.Fuse(new[] { keywordRanking, denseRanking });

        var queryTokens = new HashSet<string>(TextTokenizer.Tokenize(query), StringComparer.Ordinal);
        var reranked = fused.Select(entry =>
        {
            var chunk = byId[entry.Id];
            var chunkTokens = new HashSet<string>(TextTokenizer.Tokenize(chunk.Content), StringComparer.Ordinal);
            var overlap = queryTokens.Count == 0 ? 0.0 : (double)queryTokens.Intersect(chunkTokens).Count() / queryTokens.Count;
            var score = entry.Score + overlap * 0.1;
            return chunk with { Score = score };
        })
        .OrderByDescending(c => c.Score)
        .Take(topK)
        .ToArray();

        return reranked;
    }
}
