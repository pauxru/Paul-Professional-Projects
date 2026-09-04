using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Domain.Rules;

/// <summary>
/// Full ruleset — an ordered collection of RuleDefinitions, band thresholds,
/// merchant-level overrides. Versioned so that decisions are reproducible.
/// </summary>
public sealed record RulesetDefinition(
    string Version,
    string Name,
    IReadOnlyList<RuleDefinition> Rules,
    RiskBands Bands,
    IReadOnlyDictionary<string, MerchantPolicy> MerchantOverrides,
    int MaxScoreCap = 1000);

/// <summary>
/// Band thresholds in the aggregated 0..1000 space. Order is:
///   score less than ApproveMax   -> Approve
///   score less than StepUpMax    -> StepUp
///   score less than ReviewMax    -> Review
///   otherwise                    -> Decline
/// </summary>
public sealed record RiskBands(int ApproveMax, int StepUpMax, int ReviewMax)
{
    public Decision Classify(int score)
    {
        if (score < ApproveMax) return Decision.Approve;
        if (score < StepUpMax) return Decision.StepUp;
        if (score < ReviewMax) return Decision.Review;
        return Decision.Decline;
    }
}

public sealed record MerchantPolicy(
    string MerchantId,
    int? ScoreOffset = null,
    Decision? OverrideDecision = null,
    bool ForceReview = false);
