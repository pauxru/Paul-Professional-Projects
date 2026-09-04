using RagAssistant.Domain.Documents;
using RagAssistant.Domain.Retrieval;

namespace RagAssistant.Application.Abstractions;

public sealed record VectorSearchQuery(
    float[] Embedding,
    string EmbeddingModelId,
    int Limit,
    UserPrincipal User);

public sealed record KeywordSearchQuery(
    string Query,
    int Limit,
    UserPrincipal User);

public interface IVectorStore
{
    Task UpsertAsync(IReadOnlyList<DocumentChunk> chunks, string documentTitle, Classification classification, CancellationToken ct);
    Task RemoveByDocumentAsync(Guid documentId, CancellationToken ct);
    Task<IReadOnlyList<RetrievedChunk>> SearchAsync(VectorSearchQuery query, CancellationToken ct);
    Task<IReadOnlyList<RetrievedChunk>> KeywordSearchAsync(KeywordSearchQuery query, CancellationToken ct);
    Task<int> CountAsync(CancellationToken ct);
}
