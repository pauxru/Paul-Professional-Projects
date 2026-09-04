using AgentPlatform.Domain.Common;

namespace AgentPlatform.Domain.Rules;

/// <summary>Inputs to the refund eligibility rule (gathered by tools, not invented by a model).</summary>
public sealed record RefundEvaluationInput(
    decimal OrderAmount,
    string Currency,
    int DaysSincePurchase,
    int PriorRefundCount,
    bool ItemReturned,
    string ReasonCategory);

/// <summary>Deterministic decision produced by <see cref="RefundEligibilityCalculator"/>.</summary>
public sealed record RefundDecision(
    bool Eligible,
    Money MaxRefund,
    string Reason,
    bool RequiresManualApproval);

/// <summary>
/// Computes refund eligibility with <b>deterministic code, never a model</b>. Eligibility and the
/// refundable amount are policy decisions with money attached, so they must be auditable,
/// testable and identical every time. The model's only role in the refund workflow is to draft an
/// explanation and assemble evidence — it does not decide who gets money.
/// </summary>
public sealed class RefundEligibilityCalculator
{
    public const int StandardWindowDays = 30;
    public const int ExtendedWindowDays = 60;
    public const int DefectiveWindowDays = 90;
    public const int FraudReviewThreshold = 3;
    public const decimal HighValueApprovalThreshold = 200m;

    public RefundDecision Evaluate(RefundEvaluationInput input)
    {
        if (input.OrderAmount < 0)
            return new RefundDecision(false, Money.Zero(input.Currency), "Order amount is invalid.", false);

        var reason = (input.ReasonCategory ?? string.Empty).Trim().ToLowerInvariant();

        // Fraud guard: an unusual number of prior refunds always routes to a human.
        if (input.PriorRefundCount >= FraudReviewThreshold)
        {
            return new RefundDecision(false, Money.Zero(input.Currency),
                $"Customer has {input.PriorRefundCount} prior refunds (threshold {FraudReviewThreshold}); manual review required.",
                RequiresManualApproval: true);
        }

        // Defective items get a full refund within a longer window regardless of return.
        if (reason is "defective" or "damaged")
        {
            if (input.DaysSincePurchase <= DefectiveWindowDays)
                return Decide(input, input.OrderAmount, $"Defective/damaged item within {DefectiveWindowDays}-day window: full refund.");
            return new RefundDecision(false, Money.Zero(input.Currency),
                $"Defective claim is outside the {DefectiveWindowDays}-day window.", false);
        }

        // Standard change-of-mind returns.
        if (input.DaysSincePurchase <= StandardWindowDays)
        {
            if (input.ItemReturned)
                return Decide(input, input.OrderAmount, $"Return within {StandardWindowDays} days with item returned: full refund.");
            return Decide(input, Round(input.OrderAmount * 0.5m),
                $"Return within {StandardWindowDays} days without item returned: 50% refund pending inspection.");
        }

        if (input.DaysSincePurchase <= ExtendedWindowDays && input.ItemReturned)
            return Decide(input, Round(input.OrderAmount * 0.5m),
                $"Return within {ExtendedWindowDays} days with item returned: 50% refund.");

        return new RefundDecision(false, Money.Zero(input.Currency),
            $"Purchase is {input.DaysSincePurchase} days old and outside the refund window.", false);
    }

    private RefundDecision Decide(RefundEvaluationInput input, decimal amount, string reason)
    {
        var money = new Money(Round(amount), input.Currency);
        var requiresApproval = money.Amount >= HighValueApprovalThreshold;
        var suffix = requiresApproval ? $" Amount ≥ {HighValueApprovalThreshold} {input.Currency}; human approval required." : string.Empty;
        return new RefundDecision(true, money, reason + suffix, requiresApproval);
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}
