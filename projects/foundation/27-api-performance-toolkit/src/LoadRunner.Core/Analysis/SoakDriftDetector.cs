using LoadRunner.Core.Metrics;

namespace LoadRunner.Core.Analysis;

public sealed record SoakDrift(
    double LatencySlopeMsPerMinute,
    double LatencyR2,
    bool LatencyRegression,
    double ThroughputSlopeRpsPerMinute,
    bool ThroughputRegression,
    int WindowCount);

/// <summary>
/// Detects gradual regressions during a long-running (soak) test: does p95 latency drift
/// upward over time? Does throughput drift downward? We slice the run into equal windows
/// and fit an OLS line through the window latencies. A slope above the configured
/// threshold, combined with a decent R² so we're not just fitting noise, counts as drift.
/// </summary>
public static class SoakDriftDetector
{
    public static SoakDrift Detect(
        IReadOnlyList<TimeSeriesPoint> series,
        double latencyDriftThresholdMsPerMinute = 5.0,
        double throughputDriftThresholdRpsPerMinute = -1.0,
        double r2Floor = 0.3,
        int minWindows = 6)
    {
        if (series.Count == 0)
            return new SoakDrift(0, 0, false, 0, false, 0);

        var start = series[0].TimestampUtc;
        var latencyPoints = new List<(double t, double y)>(series.Count);
        var throughputPoints = new List<(double t, double y)>(series.Count);
        foreach (var p in series)
        {
            var t = (p.TimestampUtc - start).TotalSeconds;
            latencyPoints.Add((t, p.P95Ms));
            throughputPoints.Add((t, p.Count));
        }

        if (latencyPoints.Count < minWindows)
            return new SoakDrift(0, 0, false, 0, false, latencyPoints.Count);

        var latencyLine = LinearRegression.Fit(latencyPoints);
        var throughputLine = LinearRegression.Fit(throughputPoints);

        var latencyRegression = latencyLine.SlopePerMinute > latencyDriftThresholdMsPerMinute
                                && latencyLine.R2 >= r2Floor;
        var throughputRegression = throughputLine.SlopePerMinute < throughputDriftThresholdRpsPerMinute
                                   && throughputLine.R2 >= r2Floor;

        return new SoakDrift(
            latencyLine.SlopePerMinute,
            latencyLine.R2,
            latencyRegression,
            throughputLine.SlopePerMinute,
            throughputRegression,
            latencyPoints.Count);
    }
}
