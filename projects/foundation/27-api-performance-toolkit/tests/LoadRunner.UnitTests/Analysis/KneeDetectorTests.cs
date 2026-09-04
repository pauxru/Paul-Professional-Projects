using LoadRunner.Core.Analysis;
using Xunit;

namespace LoadRunner.UnitTests.Analysis;

public class KneeDetectorTests
{
    [Fact]
    public void DetectsThresholdCrossing_WhenP95Exceeded()
    {
        var steps = new List<StressStepResult>
        {
            new(10, 50, 0, 100),
            new(20, 55, 0, 200),
            new(30, 60, 0, 300),
            new(40, 250, 0.01, 400),   // knee here (p95 target 200ms)
            new(50, 500, 0.1, 500),
        };
        var result = KneeDetector.Detect(steps, p95ThresholdMs: 200, errorRateThreshold: null);
        Assert.Equal(40, result.BreakingPointRate);
        Assert.Equal("p95-threshold", result.Method);
    }

    [Fact]
    public void FallsBackToElbowHeuristic_WhenNoThresholdSupplied()
    {
        var steps = new List<StressStepResult>
        {
            new(10, 20, 0, 100),
            new(20, 22, 0, 200),
            new(30, 25, 0, 300),
            new(40, 35, 0, 400),
            new(50, 80, 0, 500),      // elbow point
            new(60, 300, 0.01, 500),
            new(70, 800, 0.05, 500),
        };
        var result = KneeDetector.Detect(steps, p95ThresholdMs: null, errorRateThreshold: null);
        Assert.Equal("elbow-heuristic", result.Method);
        Assert.NotNull(result.KneeRate);
    }

    [Fact]
    public void ReportsNoData_WhenSequenceEmpty()
    {
        var result = KneeDetector.Detect(new List<StressStepResult>(), null, null);
        Assert.Equal("no-data", result.Method);
        Assert.Null(result.KneeRate);
    }
}
