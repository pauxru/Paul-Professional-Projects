namespace Idp.Application.Ai;

/// <summary>A single chat message for the optional LLM-backed classifier adapter.</summary>
public sealed record ChatMessage(string Role, string Content);

/// <summary>
/// Minimal chat-model port. The default build never calls a real model: the only adapter used by
/// default is a deterministic in-repo stub. A production adapter would call a hosted model. This port
/// exists so the optional LLM classifier can be unit tested with a stubbed handler.
/// </summary>
public interface IChatModel
{
    Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken ct = default);
}
