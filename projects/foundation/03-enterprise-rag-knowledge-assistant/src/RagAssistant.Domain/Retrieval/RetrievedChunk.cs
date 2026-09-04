using RagAssistant.Domain.Documents;

namespace RagAssistant.Domain.Retrieval;

public sealed record RetrievedChunk(
    Guid ChunkId,
    Guid DocumentId,
    string DocumentTitle,
    int Sequence,
    int StartChar,
    int EndChar,
    string Content,
    double Score,
    Classification Classification);
