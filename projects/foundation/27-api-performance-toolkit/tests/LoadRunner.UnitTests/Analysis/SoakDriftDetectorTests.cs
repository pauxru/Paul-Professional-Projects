using LoadRunner.Core.Analysis;
using LoadRunner.Core.Metrics;
using Xunit;

namespace LoadRunner.UnitTests.Analysis;

public class SoakDriftDetectorTests
{
    [Fact]
    public void DetectsLatencyRegression_WhenP95TrendsUpward()
    {
        var start = DateTimeOffset.UtcNow;
        var series = new List<TimeSeriesPoint>();
        for (var i = 0; i < 60; i++)
        {
            // p95 starts at 100 ms, rises 2 ms/sec => 120 ms/min
            series.Add(new TimeSeriesPoint(start.AddSeconds(i), 100, 0, 50, 100 + 2 * i, 150 + 2 * i));
        }
        var drift = SoakDriftDetector.Detect(series, latencyDriftThresholdMsPerMinute: 5);
        Assert.True(drift.LatencyRegression);
        Assert.InRange(drift.LatencySlopeMsPerMinute, 100, 140);
    }

    [Fact]
    public void ReportsStable_ForFlatSeries()
    {
        var start = DateTimeOffset.UtcNow;
        var rng = new Random(1);
        var series = new List<TimeSeriesPoint>();
        for (var i = 0; i < 60; i++)
        {
            var noise = rng.NextDouble() * 2 - 1; // -1..1 ms noise
            series.Add(new TimeSeriesPoint(start.AddSeconds(i), 100, 0, 50, 100 + noise, 150));
        }
        var drift = SoakDriftDetector.Detect(series);
        Assert.False(drift.LatencyRegression);
    }

    [Fact]
    public void ReturnsZeroSlope_WhenSeriesTooShort()
    {
        var start = DateTimeOffset.UtcNow;
        var series = new List<TimeSeriesPoint>
        {
            new(start.AddSeconds(0), 100, 0, 50, 100, 150),
            new(start.AddSeconds(1), 100, 0, 50, 105, 155),
        };
        var drift = SoakDriftDetector.Detect(series, minWindows: 10);
        Assert.Equal(0, drift.LatencySlopeMsPerMinute);
        Assert.False(drift.LatencyRegression);
    }
}
