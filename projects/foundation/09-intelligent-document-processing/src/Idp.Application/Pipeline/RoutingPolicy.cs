using Idp.Application.Configuration;
using Idp.Domain.Documents;

namespace Idp.Application.Pipeline;

/// <summary>
/// Pure confidence-to-route mapping. A document is rejected below the reject threshold, auto-approved
/// only when it is above the auto-approve threshold AND has no hard validation failure, and otherwise
/// routed to human review. Boundary behaviour is pinned by unit tests.
/// </summary>
public static class RoutingPolicy
{
    public static RoutingDecision Decide(
        double documentConfidence, bool hasHardValidationFailure, PipelineOptions options)
    {
        if (documentConfidence < options.RejectThreshold)
            return RoutingDecision.Reject;

        if (documentConfidence >= options.AutoApproveThreshold && !hasHardValidationFailure)
            return RoutingDecision.AutoApprove;

        return RoutingDecision.Review;
    }
}

/// <summary>Prioritises review tasks by value, low confidence and (via SLA) age.</summary>
public static class ReviewPriority
{
    public static int Calculate(decimal? documentValue, double documentConfidence)
    {
        var valueComponent = documentValue is { } v ? (int)Math.Min(500, v / 100m) : 0;
        var confidenceComponent = (int)Math.Round((1.0 - documentConfidence) * 100);
        return valueComponent + confidenceComponent;
    }
}
