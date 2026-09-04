using LoadRunner.Core.Metrics;

namespace LoadRunner.Core.Analysis;

/// <summary>
/// Detects the "knee" in a stress test: the arrival-rate step at which p95 latency or
/// error rate crosses a configured threshold. Uses the classic "knee = maximum distance
/// from the line connecting the first and last measured points" heuristic when no
/// threshold is crossed — this gives a sensible answer even if the run never explicitly
/// broke the SLA.
/// </summary>
public static class KneeDetector
{
    public sealed record KneeResult(
        int? BreakingPointRate,
        int? KneeRate,
        double? BreakingP95Ms,
        string Method);

    public static KneeResult Detect(
        IReadOnlyList<StressStepResult> steps,
        double? p95ThresholdMs,
        double? errorRateThreshold)
    {
        if (steps.Count == 0) return new KneeResult(null, null, null, "no-data");

        // 1. threshold crossing
        foreach (var s in steps)
        {
            var p95Bad = p95ThresholdMs.HasValue && s.P95Ms > p95ThresholdMs.Value;
            var errBad = errorRateThreshold.HasValue && s.ErrorRate > errorRateThreshold.Value;
            if (p95Bad || errBad)
                return new KneeResult(s.RatePerSec, s.RatePerSec, s.P95Ms, p95Bad ? "p95-threshold" : "error-threshold");
        }

        // 2. maximum-distance from line heuristic
        if (steps.Count < 3)
            return new KneeResult(null, steps[^1].RatePerSec, steps[^1].P95Ms, "insufficient-steps");

        double x1 = steps[0].RatePerSec, y1 = steps[0].P95Ms;
        double x2 = steps[^1].RatePerSec, y2 = steps[^1].P95Ms;
        var lineLen = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        var bestDist = double.MinValue;
        var bestIdx = 0;
        for (var i = 0; i < steps.Count; i++)
        {
            var xi = steps[i].RatePerSec;
            var yi = steps[i].P95Ms;
            var num = Math.Abs((y2 - y1) * xi - (x2 - x1) * yi + x2 * y1 - y2 * x1);
            var dist = lineLen == 0 ? 0 : num / lineLen;
            if (dist > bestDist) { bestDist = dist; bestIdx = i; }
        }
        var knee = steps[bestIdx];
        return new KneeResult(null, knee.RatePerSec, knee.P95Ms, "elbow-heuristic");
    }
}

public sealed record StressStepResult(int RatePerSec, double P95Ms, double ErrorRate, long Count);
