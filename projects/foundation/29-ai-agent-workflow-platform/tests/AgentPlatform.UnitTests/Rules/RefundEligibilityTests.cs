using AgentPlatform.Domain.Rules;

namespace AgentPlatform.UnitTests.Rules;

/// <summary>
/// Proves refund eligibility is computed by deterministic policy code — never a model. These are the
/// money-affecting rules, so they must be identical every time and fully covered.
/// </summary>
public sealed class RefundEligibilityTests
{
    private readonly RefundEligibilityCalculator _calc = new();

    private static RefundEvaluationInput Input(decimal amount, int days, string reason,
        bool returned, int priorRefunds = 0, string currency = "USD")
        => new(amount, currency, days, priorRefunds, returned, reason);

    [Fact]
    public void Full_refund_within_standard_window_when_returned()
    {
        var d = _calc.Evaluate(Input(50m, 10, "change_of_mind", returned: true));
        Assert.True(d.Eligible);
        Assert.Equal(50m, d.MaxRefund.Amount);
    }

    [Fact]
    public void Half_refund_within_standard_window_when_not_returned()
    {
        var d = _calc.Evaluate(Input(80m, 25, "change_of_mind", returned: false));
        Assert.True(d.Eligible);
        Assert.Equal(40m, d.MaxRefund.Amount);
    }

    [Fact]
    public void Half_refund_within_extended_window_when_returned()
    {
        var d = _calc.Evaluate(Input(120m, 45, "change_of_mind", returned: true));
        Assert.True(d.Eligible);
        Assert.Equal(60m, d.MaxRefund.Amount);
    }

    [Fact]
    public void Ineligible_outside_extended_window()
        => Assert.False(_calc.Evaluate(Input(120m, 70, "change_of_mind", returned: true)).Eligible);

    [Fact]
    public void Defective_item_full_refund_within_long_window()
    {
        var d = _calc.Evaluate(Input(200m, 80, "defective", returned: false));
        Assert.True(d.Eligible);
        Assert.Equal(200m, d.MaxRefund.Amount);
    }

    [Fact]
    public void Defective_item_ineligible_beyond_long_window()
        => Assert.False(_calc.Evaluate(Input(200m, 100, "defective", returned: false)).Eligible);

    [Fact]
    public void Fraud_guard_routes_to_manual_review()
    {
        var d = _calc.Evaluate(Input(60m, 5, "change_of_mind", returned: true, priorRefunds: 3));
        Assert.False(d.Eligible);
        Assert.True(d.RequiresManualApproval);
    }

    [Fact]
    public void High_value_refund_requires_manual_approval()
    {
        var d = _calc.Evaluate(Input(350m, 10, "change_of_mind", returned: true));
        Assert.True(d.Eligible);
        Assert.True(d.RequiresManualApproval);
    }

    [Fact]
    public void Negative_amount_is_invalid()
        => Assert.False(_calc.Evaluate(Input(-5m, 1, "change_of_mind", returned: true)).Eligible);

    [Fact]
    public void Boundary_day_thirty_is_still_standard_window()
    {
        var d = _calc.Evaluate(Input(100m, 30, "change_of_mind", returned: true));
        Assert.True(d.Eligible);
        Assert.Equal(100m, d.MaxRefund.Amount);
    }
}
