using System.Text.Json.Nodes;
using AgentPlatform.Application.Abstractions;
using AgentPlatform.Domain.Rules;

namespace AgentPlatform.Infrastructure.Transforms;

/// <summary>
/// chunk_document — splits <c>state["document"]</c> into ~400-character chunks on sentence
/// boundaries. Pure, deterministic text processing (no model).
/// </summary>
public sealed class ChunkDocumentTransform : IStateTransform
{
    private const int TargetChunkChars = 400;

    public string Id => "chunk_document";

    public JsonNode? Apply(JsonObject state)
    {
        var document = (state["document"] as JsonValue)?.GetValue<string>() ?? string.Empty;
        var sentences = System.Text.RegularExpressions.Regex.Split(document.Trim(), @"(?<=[\.!\?])\s+");

        var chunks = new JsonArray();
        var current = new System.Text.StringBuilder();
        foreach (var sentence in sentences)
        {
            if (sentence.Length == 0) continue;
            if (current.Length > 0 && current.Length + sentence.Length > TargetChunkChars)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }
            current.Append(sentence).Append(' ');
        }
        if (current.Length > 0) chunks.Add(current.ToString().Trim());
        if (chunks.Count == 0 && document.Length > 0) chunks.Add(document);

        return chunks;
    }
}

/// <summary>
/// summarise_chunk — accumulator used inside the summarisation loop. Reads the loop's
/// <c>current_chunk</c> and appends a one-sentence extractive summary to <c>chunk_summaries</c>.
/// </summary>
public sealed class SummariseChunkTransform : IStateTransform
{
    public string Id => "summarise_chunk";

    public JsonNode? Apply(JsonObject state)
    {
        var chunk = (state["current_chunk"] as JsonValue)?.GetValue<string>() ?? string.Empty;
        var summary = AgentPlatform.Infrastructure.Tools.ExtractiveSummary.Summarise(chunk, 1);
        var line = summary.Count > 0 ? summary[0] : chunk;

        var accumulator = state["chunk_summaries"] as JsonArray ?? new JsonArray();
        var result = new JsonArray();
        foreach (var item in accumulator) result.Add(item?.DeepClone());
        result.Add(line);
        return result;
    }
}

/// <summary>
/// triage_context — flattens a fetched ticket into a compact text block used as the model's input
/// for classification. Pure string assembly.
/// </summary>
public sealed class TriageContextTransform : IStateTransform
{
    public string Id => "triage_context";

    public JsonNode? Apply(JsonObject state)
    {
        var ticket = state["ticket"] as JsonObject;
        if (ticket is null) return JsonValue.Create("No ticket found.");
        string Field(string k) => (ticket[k] as JsonValue)?.GetValue<string>() ?? string.Empty;
        var text = $"Subject: {Field("subject")}. Priority: {Field("priority")}. Body: {Field("body")}";
        return JsonValue.Create(text);
    }
}

/// <summary>
/// build_refund_input — assembles the deterministic refund calculator's input from the run inputs
/// and the looked-up customer (prior refund count). No eligibility decision is made here.
/// </summary>
public sealed class BuildRefundInputTransform : IStateTransform
{
    public string Id => "build_refund_input";

    public JsonNode? Apply(JsonObject state)
    {
        var customer = state["customer"] as JsonObject;
        var priorRefunds = customer?["prior_refund_count"] is JsonValue p && p.TryGetValue<int>(out var pr) ? pr : 0;

        return new JsonObject
        {
            ["order_amount"] = state["order_amount"]?.DeepClone() ?? JsonValue.Create(0),
            ["currency"] = state["currency"]?.DeepClone() ?? JsonValue.Create("USD"),
            ["days_since_purchase"] = state["days_since_purchase"]?.DeepClone() ?? JsonValue.Create(0),
            ["prior_refund_count"] = priorRefunds,
            ["item_returned"] = state["item_returned"]?.DeepClone() ?? JsonValue.Create(false),
            ["reason_category"] = state["reason_category"]?.DeepClone() ?? JsonValue.Create("change_of_mind"),
        };
    }
}

/// <summary>
/// build_proposed_action — turns the deterministic eligibility decision into the concrete refund
/// action that will be shown to a human approver and, if approved, executed.
/// </summary>
public sealed class BuildProposedActionTransform : IStateTransform
{
    public string Id => "build_proposed_action";

    public JsonNode? Apply(JsonObject state)
    {
        var eligibility = state["eligibility"] as JsonObject;
        var amount = eligibility?["max_refund_amount"]?.DeepClone() ?? JsonValue.Create(0);
        var reason = (eligibility?["reason"] as JsonValue)?.GetValue<string>() ?? "Refund";

        return new JsonObject
        {
            ["customer_id"] = state["customer_id"]?.DeepClone() ?? JsonValue.Create(string.Empty),
            ["amount_usd"] = amount,
            ["reason"] = reason,
            ["riskLevel"] = "High",
        };
    }
}

/// <summary>
/// compute_refund_eligibility — the anti-hype centrepiece: eligibility and the maximum refund
/// amount are computed by <see cref="RefundEligibilityCalculator"/> (deterministic code with money
/// attached), never by the model. Reads <c>state["refund_input"]</c>.
/// </summary>
public sealed class ComputeRefundEligibilityTransform : IStateTransform
{
    private readonly RefundEligibilityCalculator _calculator = new();

    public string Id => "compute_refund_eligibility";

    public JsonNode? Apply(JsonObject state)
    {
        var input = state["refund_input"] as JsonObject ?? new JsonObject();
        var evaluation = new RefundEvaluationInput(
            OrderAmount: Dec(input["order_amount"]),
            Currency: (input["currency"] as JsonValue)?.GetValue<string>() ?? "USD",
            DaysSincePurchase: Int(input["days_since_purchase"]),
            PriorRefundCount: Int(input["prior_refund_count"]),
            ItemReturned: (input["item_returned"] as JsonValue)?.GetValue<bool>() ?? false,
            ReasonCategory: (input["reason_category"] as JsonValue)?.GetValue<string>() ?? "change_of_mind");

        var decision = _calculator.Evaluate(evaluation);
        return new JsonObject
        {
            ["eligible"] = decision.Eligible,
            ["max_refund_amount"] = decision.MaxRefund.Amount,
            ["currency"] = decision.MaxRefund.Currency,
            ["reason"] = decision.Reason,
            ["requires_manual_approval"] = decision.RequiresManualApproval,
        };
    }

    private static decimal Dec(JsonNode? node) => node is JsonValue v && v.TryGetValue<decimal>(out var d) ? d
        : node is JsonValue v2 && v2.TryGetValue<double>(out var db) ? (decimal)db : 0m;

    private static int Int(JsonNode? node) => node is JsonValue v && v.TryGetValue<int>(out var i) ? i
        : node is JsonValue v2 && v2.TryGetValue<double>(out var d) ? (int)d : 0;
}

/// <summary>
/// extract_fields — deterministically derives a structured extraction (document type, summary,
/// confidence) from the document and its per-chunk summaries. Pure code; the model never invents
/// these fields. Confidence is lowered for very short or explicitly-flagged documents.
/// </summary>
public sealed class ExtractFieldsTransform : IStateTransform
{
    public string Id => "extract_fields";

    public JsonNode? Apply(JsonObject state)
    {
        var document = (state["document"] as JsonValue)?.GetValue<string>() ?? string.Empty;
        var lower = document.ToLowerInvariant();

        var documentType =
            lower.Contains("invoice") || lower.Contains("amount due") ? "invoice" :
            lower.Contains("agreement") || lower.Contains("hereby") || lower.Contains("party") ? "contract" :
            lower.Contains("dear") || lower.Contains("regards") ? "email" :
            "report";

        var summaries = state["chunk_summaries"] as JsonArray ?? new JsonArray();
        var summary = string.Join(" ", summaries.Select(s => (s as JsonValue)?.GetValue<string>() ?? string.Empty)).Trim();
        if (string.IsNullOrWhiteSpace(summary))
            summary = document.Length > 160 ? document[..160] : document;

        double confidence = document.Length >= 200 ? 0.9 : 0.4;
        if (lower.Contains("[[low_confidence]]")) confidence = 0.3;

        return new JsonObject
        {
            ["document_type"] = documentType,
            ["summary"] = summary,
            ["confidence"] = confidence,
        };
    }
}

public sealed class ValidateExtractionTransform : IStateTransform
{
    private static readonly string[] RequiredFields = { "document_type", "summary" };
    private const double ConfidenceThreshold = 0.6;

    public string Id => "validate_extraction";

    public JsonNode? Apply(JsonObject state)
    {
        var extraction = state["extraction"] as JsonObject;
        var issues = new JsonArray();

        if (extraction is null)
        {
            issues.Add("extraction is missing or not an object");
            return new JsonObject { ["valid"] = false, ["low_confidence"] = true, ["issues"] = issues };
        }

        foreach (var field in RequiredFields)
        {
            var value = extraction[field] as JsonValue;
            if (value is null || string.IsNullOrWhiteSpace(value.GetValue<string?>()))
                issues.Add($"missing required field '{field}'");
        }

        var confidence = extraction["confidence"] is JsonValue c && c.TryGetValue<double>(out var cv) ? cv : 0.0;
        var lowConfidence = confidence < ConfidenceThreshold;
        if (lowConfidence) issues.Add($"confidence {confidence:0.00} is below threshold {ConfidenceThreshold:0.00}");

        return new JsonObject
        {
            ["valid"] = issues.Count == 0,
            ["low_confidence"] = lowConfidence,
            ["confidence"] = confidence,
            ["issues"] = issues,
        };
    }
}
