using Contoso.Storefront.Application.Deployment;
using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Application.Release;

namespace Contoso.Storefront.UnitTests;

public sealed class CanaryAndFeatureTests
{
    private static readonly CanaryThresholds Thresholds = new(0.02, 500, 20);

    [Fact]
    public void CanaryGate_WithHealthyMetrics_Promotes()
    {
        var result = CanaryGateEvaluator.Evaluate(
            new CanaryMetrics(0.01, 250, 100),
            Thresholds);

        Assert.Equal(CanaryDecision.Promote, result.Decision);
    }

    [Fact]
    public void CanaryGate_WithErrorRateBreach_RollsBack()
    {
        var result = CanaryGateEvaluator.Evaluate(
            new CanaryMetrics(0.021, 250, 100),
            Thresholds);

        Assert.Equal(CanaryDecision.Rollback, result.Decision);
        Assert.Contains("Error rate", result.Reason);
    }

    [Fact]
    public void CanaryGate_WithLatencyBreach_RollsBack()
    {
        var result = CanaryGateEvaluator.Evaluate(
            new CanaryMetrics(0.01, 501, 100),
            Thresholds);

        Assert.Equal(CanaryDecision.Rollback, result.Decision);
        Assert.Contains("latency", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanaryGate_WithMissingMetrics_RollsBackConservatively()
    {
        var result = CanaryGateEvaluator.Evaluate(null, Thresholds);

        Assert.Equal(CanaryDecision.Rollback, result.Decision);
        Assert.Contains("missing", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanaryGate_WithTooFewSamples_RollsBackConservatively()
    {
        var result = CanaryGateEvaluator.Evaluate(
            new CanaryMetrics(0, 10, 19),
            Thresholds);

        Assert.Equal(CanaryDecision.Rollback, result.Decision);
        Assert.Contains("samples", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanaryGate_WithNonFiniteMetric_RollsBack()
    {
        var result = CanaryGateEvaluator.Evaluate(
            new CanaryMetrics(double.NaN, 10, 100),
            Thresholds);

        Assert.Equal(CanaryDecision.Rollback, result.Decision);
    }

    [Fact]
    public void PricingPreview_WithFlagDisabled_DoesNotRunCandidatePath()
    {
        var service = new PricingPreviewService(new StaticFeatureFlags(false));
        var result = service.Preview(Product());

        Assert.False(result.DarkLaunchEvaluated);
        Assert.Null(result.CandidatePrice);
        Assert.Equal(100m, result.CurrentPrice);
    }

    [Fact]
    public void PricingPreview_WithDarkLaunchEnabled_ComputesCandidateButServesStablePrice()
    {
        var service = new PricingPreviewService(new StaticFeatureFlags(true));
        var result = service.Preview(Product());

        Assert.True(result.DarkLaunchEvaluated);
        Assert.Equal(95m, result.CandidatePrice);
        Assert.Equal(100m, result.CurrentPrice);
    }

    private static ProductSnapshot Product() =>
        new(Guid.NewGuid(), "SKU", "Name", "Description", 100, "USD", true);

    private sealed class StaticFeatureFlags(bool enabled) : IFeatureFlagProvider
    {
        public bool IsEnabled(string flagName) => enabled;
    }
}
