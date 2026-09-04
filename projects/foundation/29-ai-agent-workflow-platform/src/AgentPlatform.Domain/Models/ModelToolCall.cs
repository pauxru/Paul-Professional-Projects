namespace AgentPlatform.Domain.Models;

/// <summary>
/// A single tool/function call requested by the model. <see cref="ArgumentsJson"/> is the raw
/// JSON string exactly as the model produced it — it is never trusted and is schema-validated
/// before any tool executes.
/// </summary>
public sealed record ModelToolCall(string Id, string ToolName, string ArgumentsJson);

/// <summary>Token accounting for a single model call.</summary>
public readonly record struct TokenUsage(int PromptTokens, int CompletionTokens)
{
    public int TotalTokens => PromptTokens + CompletionTokens;

    public static TokenUsage Zero => new(0, 0);

    public TokenUsage Add(TokenUsage other) =>
        new(PromptTokens + other.PromptTokens, CompletionTokens + other.CompletionTokens);
}
