namespace Contoso.Storefront.Application.Deployment;

public sealed record CanaryMetrics(double ErrorRate, double P95LatencyMs, long SampleCount);

public sealed record CanaryThresholds(
    double MaximumErrorRate,
    double MaximumP95LatencyMs,
    long MinimumSampleCount);

public enum CanaryDecision
{
    Promote,
    Rollback
}

public sealed record CanaryGateResult(CanaryDecision Decision, string Reason);

public static class CanaryGateEvaluator
{
    public static CanaryGateResult Evaluate(CanaryMetrics? metrics, CanaryThresholds thresholds)
    {
        if (metrics is null)
        {
            return new CanaryGateResult(CanaryDecision.Rollback, "Metrics are missing.");
        }

        if (metrics.SampleCount < thresholds.MinimumSampleCount)
        {
            return new CanaryGateResult(
                CanaryDecision.Rollback,
                $"Only {metrics.SampleCount} samples were available; {thresholds.MinimumSampleCount} are required.");
        }

        if (!double.IsFinite(metrics.ErrorRate) || !double.IsFinite(metrics.P95LatencyMs))
        {
            return new CanaryGateResult(CanaryDecision.Rollback, "Metrics contain non-finite values.");
        }

        if (metrics.ErrorRate > thresholds.MaximumErrorRate)
        {
            return new CanaryGateResult(
                CanaryDecision.Rollback,
                $"Error rate {metrics.ErrorRate:P2} exceeded {thresholds.MaximumErrorRate:P2}.");
        }

        if (metrics.P95LatencyMs > thresholds.MaximumP95LatencyMs)
        {
            return new CanaryGateResult(
                CanaryDecision.Rollback,
                $"P95 latency {metrics.P95LatencyMs:F0} ms exceeded {thresholds.MaximumP95LatencyMs:F0} ms.");
        }

        return new CanaryGateResult(CanaryDecision.Promote, "Error rate and latency are within policy.");
    }
}
