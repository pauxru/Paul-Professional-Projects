using RagAssistant.Application.Abstractions;
using RagAssistant.Application.Chunking;
using RagAssistant.Application.Common;
using RagAssistant.Domain.Common;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Application.Ingestion;

public sealed record IngestionRequest(
    string Title,
    string Source,
    string Content,
    AccessControlList Acl,
    ChunkingStrategy Strategy = ChunkingStrategy.SentenceAware);

public sealed record IngestionResult(Guid DocumentId, string ContentHash, int Chunks, bool ReusedExisting);

public sealed class IngestionService
{
    private readonly IDocumentRepository _repository;
    private readonly IVectorStore _vectorStore;
    private readonly IChunkerFactory _chunkerFactory;
    private readonly IEmbeddingModel _embeddingModel;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public IngestionService(
        IDocumentRepository repository,
        IVectorStore vectorStore,
        IChunkerFactory chunkerFactory,
        IEmbeddingModel embeddingModel,
        IClock clock,
        IIdGenerator ids)
    {
        _repository = repository;
        _vectorStore = vectorStore;
        _chunkerFactory = chunkerFactory;
        _embeddingModel = embeddingModel;
        _clock = clock;
        _ids = ids;
    }

    public async Task<IngestionResult> IngestAsync(IngestionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Content))
        {
            throw new ArgumentException("Content is required.", nameof(request));
        }

        var normalized = TextTokenizer.NormalizeWhitespace(request.Content);
        var hash = Hashing.ComputeContentHash(normalized);

        var existing = await _repository.GetByHashAsync(hash, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return new IngestionResult(existing.Id, hash, existing.Chunks.Count, ReusedExisting: true);
        }

        var chunker = _chunkerFactory.Create(request.Strategy);
        var spans = chunker.Chunk(normalized);
        var texts = spans.Select(s => s.Text).ToArray();
        var embeddings = await _embeddingModel.EmbedBatchAsync(texts, ct).ConfigureAwait(false);

        var docId = _ids.NewId();
        var document = new Document(
            docId,
            request.Title,
            request.Source,
            normalized,
            hash,
            request.Acl,
            _clock.UtcNow);

        var chunks = new List<DocumentChunk>(spans.Count);
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var chunk = new DocumentChunk(
                _ids.NewId(),
                docId,
                span.Sequence,
                span.StartChar,
                span.EndChar,
                span.Text,
                embeddings[i],
                _embeddingModel.ModelId,
                _embeddingModel.Dimensions);
            chunks.Add(chunk);
        }

        document.ReplaceChunks(chunks);
        await _repository.AddAsync(document, ct).ConfigureAwait(false);
        await _vectorStore.UpsertAsync(chunks, document.Title, document.Acl.Classification, ct).ConfigureAwait(false);
        return new IngestionResult(document.Id, hash, chunks.Count, ReusedExisting: false);
    }

    public async Task ReindexAsync(Guid documentId, ChunkingStrategy strategy, CancellationToken ct)
    {
        var document = await _repository.GetByIdAsync(documentId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Document {documentId} not found.");

        var chunker = _chunkerFactory.Create(strategy);
        var spans = chunker.Chunk(document.Content);
        var embeddings = await _embeddingModel.EmbedBatchAsync(spans.Select(s => s.Text).ToArray(), ct).ConfigureAwait(false);

        var chunks = new List<DocumentChunk>();
        for (var i = 0; i < spans.Count; i++)
        {
            var span = spans[i];
            var chunk = new DocumentChunk(
                _ids.NewId(),
                document.Id,
                span.Sequence,
                span.StartChar,
                span.EndChar,
                span.Text,
                embeddings[i],
                _embeddingModel.ModelId,
                _embeddingModel.Dimensions);
            chunks.Add(chunk);
        }

        document.ReplaceChunks(chunks);
        await _repository.UpdateAsync(document, ct).ConfigureAwait(false);
        await _vectorStore.RemoveByDocumentAsync(document.Id, ct).ConfigureAwait(false);
        await _vectorStore.UpsertAsync(chunks, document.Title, document.Acl.Classification, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(Guid documentId, CancellationToken ct)
    {
        await _vectorStore.RemoveByDocumentAsync(documentId, ct).ConfigureAwait(false);
        await _repository.DeleteAsync(documentId, ct).ConfigureAwait(false);
    }
}
