using AgentPlatform.Domain.Models;

namespace AgentPlatform.Application.Abstractions;

/// <summary>
/// Provider-agnostic chat model port shaped around function/tool calling. The default
/// implementation is a deterministic, offline mock; OpenAI/Azure adapters implement the same
/// contract but are never reached unless explicitly configured.
/// </summary>
public interface IChatModel
{
    /// <summary>Stable identifier of the underlying model, recorded in every trace.</summary>
    string ModelId { get; }

    Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken cancellationToken);
}
