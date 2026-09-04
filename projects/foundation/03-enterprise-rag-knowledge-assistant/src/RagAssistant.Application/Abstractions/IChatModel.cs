namespace RagAssistant.Application.Abstractions;

public sealed record ChatMessagePart(string Role, string Content);

public sealed record ChatRequest(
    IReadOnlyList<ChatMessagePart> Messages,
    double Temperature = 0.0,
    int MaxTokens = 512);

public sealed record ChatCompletion(
    string Content,
    int PromptTokens,
    int CompletionTokens,
    string Model,
    string FinishReason);

public interface IChatModel
{
    string ModelId { get; }
    Task<ChatCompletion> CompleteAsync(ChatRequest req, CancellationToken ct);
}
