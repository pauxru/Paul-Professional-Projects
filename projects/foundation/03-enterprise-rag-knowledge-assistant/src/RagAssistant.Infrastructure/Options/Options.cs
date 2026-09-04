using System.ComponentModel.DataAnnotations;

namespace RagAssistant.Infrastructure.Options;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; set; } = "Sqlite";

    [Required]
    public string ConnectionString { get; set; } = "Data Source=rag.db";
}

public sealed class AiOptions
{
    public const string SectionName = "Ai";

    [Required]
    public string Provider { get; set; } = "Local";

    public int EmbeddingDimensions { get; set; } = 384;

    public string? OpenAiEndpoint { get; set; }
    public string? OpenAiApiKey { get; set; }
    public string OpenAiChatModel { get; set; } = "gpt-4o-mini";
    public string OpenAiEmbeddingModel { get; set; } = "text-embedding-3-small";
}

public sealed class BudgetOptions
{
    public const string SectionName = "Budget";

    public decimal DailyLimitUsd { get; set; } = 10m;
    public int DailyRequestLimit { get; set; } = 500;
}

public sealed class RagOptions
{
    public const string SectionName = "Rag";

    public int ChunkMaxChars { get; set; } = 600;
    public int ChunkOverlap { get; set; } = 100;
    public int TopK { get; set; } = 4;
    public double MinRetrievalScore { get; set; } = 0.05;
    public double MinSupportForSentence { get; set; } = 0.25;
    public double MinSupportRatio { get; set; } = 0.4;
    public string DefaultChunkingStrategy { get; set; } = "SentenceAware";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "rag-assistant";

    [Required]
    public string Audience { get; set; } = "rag-assistant-clients";

    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = string.Empty;
}
