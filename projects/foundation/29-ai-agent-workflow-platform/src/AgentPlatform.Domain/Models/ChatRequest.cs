namespace AgentPlatform.Domain.Models;

/// <summary>
/// Describes a tool exposed to the model for this request: a name, a human description and the
/// JSON-schema string for its parameters. The model sees only these shapes — never executable code.
/// </summary>
public sealed record ChatToolDefinition(string Name, string Description, string ParametersSchemaJson);

/// <summary>Options for a model call.</summary>
public sealed record ChatOptions
{
    public double Temperature { get; init; } = 0.0;
    public int MaxOutputTokens { get; init; } = 1024;
    public string? PromptVersion { get; init; }
    public IReadOnlyList<string> ToolChoice { get; init; } = Array.Empty<string>();
}

/// <summary>A request to a chat model: the conversation plus the tools it may call.</summary>
public sealed record ChatRequest
{
    public required IReadOnlyList<ChatMessage> Messages { get; init; }
    public IReadOnlyList<ChatToolDefinition> Tools { get; init; } = Array.Empty<ChatToolDefinition>();
    public ChatOptions Options { get; init; } = new();
    public string? WorkflowIntent { get; init; }
}

/// <summary>A model's response: text and/or tool calls, a finish reason and token usage.</summary>
public sealed record ChatCompletion
{
    public string? Content { get; init; }
    public IReadOnlyList<ModelToolCall> ToolCalls { get; init; } = Array.Empty<ModelToolCall>();
    public FinishReason FinishReason { get; init; } = FinishReason.Stop;
    public TokenUsage Usage { get; init; } = TokenUsage.Zero;
    public string ModelId { get; init; } = "unknown";

    public bool HasToolCalls => ToolCalls.Count > 0;
}
