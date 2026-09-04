using RagAssistant.Application.Retrieval;

namespace RagAssistant.Api.Contracts;

public sealed record IngestDocumentRequest(
    string Title,
    string Source,
    string Content,
    string Classification,
    IReadOnlyList<string>? Roles,
    IReadOnlyList<string>? Departments,
    string? Strategy);

public sealed record DocumentResponse(
    Guid Id,
    string Title,
    string Source,
    string Classification,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Departments,
    int ChunkCount,
    int Version,
    DateTimeOffset UpdatedAt);

public sealed record QueryRequest(
    string Query,
    string Mode = "Hybrid",
    int TopK = 4,
    string Tenant = "default");

public sealed record CitationResponse(
    Guid DocumentId,
    string DocumentTitle,
    Guid ChunkId,
    int Sequence,
    int StartChar,
    int EndChar,
    double Score);

public sealed record QueryResponse(
    string Answer,
    bool Refused,
    string RefusalReason,
    double SupportRatio,
    IReadOnlyList<CitationResponse> Citations,
    string Mode,
    string PromptVersion,
    int PromptTokens,
    int CompletionTokens);

public sealed record ChatRequestDto(
    Guid? SessionId,
    string Query,
    string Mode = "Hybrid",
    int TopK = 4,
    string Tenant = "default");

public sealed record ChatResponse(
    Guid SessionId,
    string RewrittenQuery,
    QueryResponse Answer);

public sealed record FeedbackRequestDto(
    string Query,
    string Answer,
    int Rating,
    string? Reason,
    string PromptVersion,
    IReadOnlyList<Guid>? CitedChunkIds);

public sealed record PromptRequestDto(string Name, string Version, string Body);

public sealed record PromptResponse(Guid Id, string Name, string Version, string Hash, bool IsActive, DateTimeOffset CreatedAt);

public sealed record EvaluationResponse(
    IReadOnlyList<RetrievalMetricsDto> RetrievalMetrics,
    double CitationPrecision,
    double CitationRecall,
    double AnyCorrectCitationRate,
    double RefusalAccuracy,
    int Examples,
    IReadOnlyList<RetrievalSliceDto> Slices);

public sealed record RetrievalMetricsDto(string Mode, double RecallAtK, double MeanReciprocalRank, double NdcgAtK, int K);

public sealed record RetrievalSliceDto(string Slice, IReadOnlyList<RetrievalMetricsDto> RetrievalMetrics, int Examples);

public static class ModeParsing
{
    public static RetrievalMode Parse(string mode)
    {
        return mode?.Trim().ToLowerInvariant() switch
        {
            "keyword" => RetrievalMode.Keyword,
            "dense" => RetrievalMode.Dense,
            "hybrid" or null or "" => RetrievalMode.Hybrid,
            _ => throw new ArgumentException($"Unknown retrieval mode '{mode}'."),
        };
    }
}
