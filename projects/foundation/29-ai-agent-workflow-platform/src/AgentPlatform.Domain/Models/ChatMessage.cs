namespace AgentPlatform.Domain.Models;

/// <summary>
/// One message in a conversation. Assistant messages may carry <see cref="ToolCalls"/>;
/// tool messages carry a <see cref="ToolCallId"/> linking the result back to the call.
/// </summary>
public sealed record ChatMessage
{
    public required ChatRole Role { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<ModelToolCall> ToolCalls { get; init; } = Array.Empty<ModelToolCall>();
    public string? ToolCallId { get; init; }
    public string? Name { get; init; }

    public static ChatMessage System(string content) => new() { Role = ChatRole.System, Content = content };
    public static ChatMessage User(string content) => new() { Role = ChatRole.User, Content = content };
    public static ChatMessage Assistant(string content) => new() { Role = ChatRole.Assistant, Content = content };

    public static ChatMessage AssistantToolCalls(IReadOnlyList<ModelToolCall> calls) =>
        new() { Role = ChatRole.Assistant, ToolCalls = calls };

    public static ChatMessage ToolResult(string toolCallId, string name, string content) =>
        new() { Role = ChatRole.Tool, ToolCallId = toolCallId, Name = name, Content = content };
}
