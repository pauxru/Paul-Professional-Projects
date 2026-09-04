namespace RagAssistant.Domain.Retrieval;

public sealed record Citation(
    Guid DocumentId,
    string DocumentTitle,
    Guid ChunkId,
    int Sequence,
    int StartChar,
    int EndChar,
    double Score);
