using LoadRunner.Core.Assertions;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;
using Xunit;

namespace LoadRunner.UnitTests.Assertions;

public class ThresholdEvaluatorTests
{
    private static RunSnapshot MakeSnapshot(double p95, double errorRate, double rps = 100)
    {
        var lat = new LatencyStats(50, 60, 70, p95, 200, 400, 5, 500, 60, 20);
        var stats = new StepStats("agg", 1000, (long)(errorRate * 1000), errorRate, rps, 0, 0, 0, 0, 0, 1024, lat, lat);
        var step = new StepStats("CreateOrder", 1000, (long)(errorRate * 1000), errorRate, rps, 0, 0, 0, 0, 0, 1024, lat, lat);
        return new RunSnapshot(
            DateTimeOffset.UtcNow.AddSeconds(-10),
            DateTimeOffset.UtcNow,
            TimeSpan.Zero,
            stats,
            new[] { step },
            Array.Empty<TimeSeriesPoint>(),
            0);
    }

    [Fact]
    public void PassesWhenP95BelowTarget()
    {
        var snap = MakeSnapshot(p95: 150, errorRate: 0.005);
        var results = ThresholdEvaluator.Evaluate(
            new[] { new AssertionSpec("latency.p95", "<", 300), new AssertionSpec("error_rate", "<", 0.01) },
            snap);
        Assert.All(results, r => Assert.True(r.Passed));
    }

    [Fact]
    public void FailsWhenP95Exceeded()
    {
        var snap = MakeSnapshot(p95: 400, errorRate: 0.005);
        var results = ThresholdEvaluator.Evaluate(
            new[] { new AssertionSpec("latency.p95", "<", 300) },
            snap);
        Assert.False(results[0].Passed);
    }

    [Fact]
    public void ResolvesPerStepMetric()
    {
        var snap = MakeSnapshot(p95: 250, errorRate: 0);
        var results = ThresholdEvaluator.Evaluate(
            new[] { new AssertionSpec("step:CreateOrder:p95", "<", 300) },
            snap);
        Assert.True(results[0].Passed);
        Assert.Equal(250, results[0].Actual);
    }

    [Fact]
    public void ThrowsOnUnknownOperator()
    {
        var snap = MakeSnapshot(p95: 100, errorRate: 0);
        Assert.Throws<InvalidOperationException>(() =>
            ThresholdEvaluator.Evaluate(new[] { new AssertionSpec("latency.p95", "~", 0) }, snap));
    }
}
