using Idp.Application.Ai;
using Idp.Application.Classification;
using Idp.Application.Documents;
using Idp.Domain.Documents;

namespace Idp.Infrastructure.Classification;

/// <summary>
/// Optional LLM-backed classifier adapter. It is NEVER selected by default (see
/// <c>Classifier:Provider</c>); the deterministic <see cref="RulesDocumentClassifier"/> is the
/// default. This adapter exists to demonstrate the port/adapter seam and is unit tested against a
/// stubbed <see cref="IChatModel"/> so no network call is ever made in the build or tests.
/// </summary>
public sealed class ChatModelClassifier : IDocumentClassifier
{
    private readonly IChatModel _chat;

    public ChatModelClassifier(IChatModel chat) => _chat = chat;

    public ClassificationResult Classify(DocumentContent content)
    {
        var snippet = (content.RawText ?? string.Empty);
        if (snippet.Length > 1200) snippet = snippet[..1200];

        var messages = new List<ChatMessage>
        {
            new("system",
                "You are a document classifier. Reply with exactly one of: " +
                "Invoice, PurchaseOrder, DeliveryNote, Unknown."),
            new("user", snippet)
        };

        // Synchronously resolve; the stub completes synchronously and no network is involved.
        var answer = _chat.CompleteAsync(messages).GetAwaiter().GetResult()?.Trim() ?? "Unknown";

        var type = answer.ToLowerInvariant() switch
        {
            "invoice" => DocumentType.Invoice,
            "purchaseorder" or "purchase order" => DocumentType.PurchaseOrder,
            "deliverynote" or "delivery note" => DocumentType.DeliveryNote,
            _ => DocumentType.Unknown
        };

        var confidence = type == DocumentType.Unknown ? 0.2 : 0.75;
        var features = new[] { new ClassificationFeature($"llm:{answer}", 1.0, 1.0) };
        var scores = new Dictionary<DocumentType, double> { [type] = confidence };
        return new ClassificationResult(
            type, confidence, $"LLM adapter classified as {type} (stubbed, offline).",
            features, scores);
    }
}
