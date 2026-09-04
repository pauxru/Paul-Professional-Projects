using Idp.Application.Ai;

namespace Idp.Infrastructure.Ai;

/// <summary>
/// Deterministic, in-repo <see cref="IChatModel"/>. The default build NEVER calls a hosted model;
/// this stub returns a canned classification-style answer derived from simple keyword heuristics so
/// the optional LLM-backed classifier can be exercised without any network call. It exists purely to
/// make the optional adapter testable and demonstrable offline.
/// </summary>
public sealed class StubChatModel : IChatModel
{
    public Task<string> CompleteAsync(
        IReadOnlyList<ChatMessage> messages, CancellationToken ct = default)
    {
        var text = string.Join("\n", messages.Select(m => m.Content)).ToLowerInvariant();
        var answer =
            text.Contains("delivery note") || text.Contains("goods received") ? "DeliveryNote" :
            text.Contains("purchase order") || text.Contains("po number") ? "PurchaseOrder" :
            text.Contains("invoice") ? "Invoice" :
            "Unknown";
        return Task.FromResult(answer);
    }
}
