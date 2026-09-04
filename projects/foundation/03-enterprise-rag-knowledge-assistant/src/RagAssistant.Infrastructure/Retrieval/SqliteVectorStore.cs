using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Embeddings;
using RagAssistant.Application.Retrieval;
using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Retrieval;
using RagAssistant.Infrastructure.Persistence;

namespace RagAssistant.Infrastructure.Retrieval;

public sealed class SqliteVectorStore : IVectorStore
{
    private readonly IDbContextFactory<RagDbContext> _factory;
    private readonly ConcurrentDictionary<Guid, ChunkRecord> _cache = new();
    private Bm25Index _bm25 = new();
    private readonly object _bm25Lock = new();
    private volatile bool _loaded;

    public SqliteVectorStore(IDbContextFactory<RagDbContext> factory)
    {
        _factory = factory;
    }

    public async Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, string documentTitle, Classification classification, CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        foreach (var chunk in chunks)
        {
            var record = new ChunkRecord(chunk.Id, chunk.DocumentId, documentTitle, chunk.Sequence, chunk.StartChar, chunk.EndChar, chunk.Content, chunk.DecodeEmbedding(), classification);
            _cache[chunk.Id] = record;
            lock (_bm25Lock)
            {
                _bm25.Add(new Bm25Document(chunk.Id.ToString("N"), chunk.Content));
            }
        }
    }

    public Task RemoveByDocumentAsync(Guid documentId, CancellationToken ct)
    {
        foreach (var kv in _cache.Where(kv => kv.Value.DocumentId == documentId).ToArray())
        {
            _cache.TryRemove(kv.Key, out _);
        }

        RebuildBm25();
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<RetrievedChunk>> SearchAsync(VectorSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        var results = new List<RetrievedChunk>();
        foreach (var record in _cache.Values)
        {
            if (!AclCheck(record, query.User))
            {
                continue;
            }

            if (record.Embedding.Length != query.Embedding.Length)
            {
                continue;
            }

            var score = VectorMath.CosineSimilarity(query.Embedding, record.Embedding);
            results.Add(new RetrievedChunk(
                record.ChunkId,
                record.DocumentId,
                record.DocumentTitle,
                record.Sequence,
                record.StartChar,
                record.EndChar,
                record.Content,
                score,
                record.Classification));
        }

        return results
            .OrderByDescending(r => r.Score)
            .Take(Math.Max(1, query.Limit))
            .ToArray();
    }

    public async Task<IReadOnlyList<RetrievedChunk>> KeywordSearchAsync(KeywordSearchQuery query, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        await EnsureLoadedAsync(ct).ConfigureAwait(false);

        IReadOnlyList<Bm25Hit> hits;
        lock (_bm25Lock)
        {
            hits = _bm25.Search(query.Query, Math.Max(query.Limit * 3, 16));
        }

        var results = new List<RetrievedChunk>();
        foreach (var hit in hits)
        {
            if (!Guid.TryParseExact(hit.Id, "N", out var chunkId))
            {
                continue;
            }

            if (!_cache.TryGetValue(chunkId, out var record))
            {
                continue;
            }

            if (!AclCheck(record, query.User))
            {
                continue;
            }

            results.Add(new RetrievedChunk(
                record.ChunkId,
                record.DocumentId,
                record.DocumentTitle,
                record.Sequence,
                record.StartChar,
                record.EndChar,
                record.Content,
                hit.Score,
                record.Classification));

            if (results.Count >= query.Limit)
            {
                break;
            }
        }

        return results;
    }

    public async Task<int> CountAsync(CancellationToken ct)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _cache.Count;
    }

    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_loaded)
        {
            return;
        }

        await using var db = await _factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var chunks = await db.Chunks
            .AsNoTracking()
            .Join(db.Documents.AsNoTracking(), c => c.DocumentId, d => d.Id, (c, d) => new
            {
                c.Id,
                c.DocumentId,
                DocumentTitle = d.Title,
                Classification = d.Acl.Classification,
                c.Sequence,
                c.StartChar,
                c.EndChar,
                c.Content,
                c.Embedding,
                c.EmbeddingDimensions,
            })
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        foreach (var chunk in chunks)
        {
            var vector = DecodeEmbedding(chunk.Embedding, chunk.EmbeddingDimensions);
            var record = new ChunkRecord(
                chunk.Id,
                chunk.DocumentId,
                chunk.DocumentTitle,
                chunk.Sequence,
                chunk.StartChar,
                chunk.EndChar,
                chunk.Content,
                vector,
                chunk.Classification);
            _cache[chunk.Id] = record;
        }

        RebuildBm25();
        _loaded = true;
    }

    private void RebuildBm25()
    {
        lock (_bm25Lock)
        {
            var index = new Bm25Index();
            index.Add(_cache.Values.Select(v => new Bm25Document(v.ChunkId.ToString("N"), v.Content)));
            _bm25 = index;
        }
    }

    private static bool AclCheck(ChunkRecord record, UserPrincipal user)
        => user.MaxClassification >= record.Classification;

    private static float[] DecodeEmbedding(byte[] payload, int dims)
    {
        var vector = new float[dims];
        if (payload.Length != dims * sizeof(float))
        {
            return vector;
        }

        Buffer.BlockCopy(payload, 0, vector, 0, payload.Length);
        return vector;
    }

    private sealed record ChunkRecord(
        Guid ChunkId,
        Guid DocumentId,
        string DocumentTitle,
        int Sequence,
        int StartChar,
        int EndChar,
        string Content,
        float[] Embedding,
        Classification Classification);
}
