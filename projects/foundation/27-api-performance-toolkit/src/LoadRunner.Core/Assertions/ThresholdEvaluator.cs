using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;

namespace LoadRunner.Core.Assertions;

public sealed record AssertionResult(AssertionSpec Spec, double Actual, bool Passed, string Description);

public static class ThresholdEvaluator
{
    public static IReadOnlyList<AssertionResult> Evaluate(
        IReadOnlyList<AssertionSpec> assertions,
        RunSnapshot snapshot)
    {
        var results = new List<AssertionResult>(assertions.Count);
        foreach (var spec in assertions)
        {
            var actual = ResolveMetric(spec.Metric, snapshot);
            var passed = Compare(actual, spec.Op, spec.Value);
            results.Add(new AssertionResult(spec, actual, passed,
                $"{spec.Metric} {spec.Op} {spec.Value} => actual={actual:F3} => {(passed ? "PASS" : "FAIL")}"));
        }
        return results;
    }

    public static bool Compare(double actual, string op, double target) => op switch
    {
        "<" => actual < target,
        "<=" => actual <= target,
        ">" => actual > target,
        ">=" => actual >= target,
        "==" => Math.Abs(actual - target) < 1e-9,
        "!=" => Math.Abs(actual - target) >= 1e-9,
        _ => throw new InvalidOperationException($"Unknown operator '{op}'")
    };

    /// <summary>
    /// Metric keys understood by the threshold engine.
    /// Examples:
    ///   * <c>latency.p95</c>, <c>latency.p99</c> — aggregate service latency in ms
    ///   * <c>intended.p95</c> — aggregate intended-start latency in ms
    ///   * <c>error_rate</c> — fraction 0..1
    ///   * <c>rps</c> — requests per second
    ///   * <c>step:CreateOrder:p95</c> — per-step latency
    ///   * <c>step:CreateOrder:error_rate</c> — per-step error rate
    /// </summary>
    public static double ResolveMetric(string metric, RunSnapshot snapshot)
    {
        var parts = metric.Split(':');
        if (parts.Length == 3 && parts[0].Equals("step", StringComparison.OrdinalIgnoreCase))
        {
            var stepName = parts[1];
            var suffix = parts[2];
            var step = snapshot.PerStep.FirstOrDefault(s => s.StepName.Equals(stepName, StringComparison.OrdinalIgnoreCase))
                       ?? throw new InvalidOperationException($"Step '{stepName}' not found in snapshot");
            return ResolveStepMetric(suffix, step);
        }

        if (metric.StartsWith("intended.", StringComparison.OrdinalIgnoreCase))
            return ResolveLatencyMetric(metric.Substring("intended.".Length), snapshot.Aggregate.Intended);

        if (metric.StartsWith("latency.", StringComparison.OrdinalIgnoreCase))
            return ResolveLatencyMetric(metric.Substring("latency.".Length), snapshot.Aggregate.Service);

        return metric.ToLowerInvariant() switch
        {
            "error_rate" => snapshot.Aggregate.ErrorRate,
            "errors" => snapshot.Aggregate.Errors,
            "count" => snapshot.Aggregate.Count,
            "rps" => snapshot.Aggregate.ThroughputRps,
            _ => throw new InvalidOperationException($"Unknown metric '{metric}'")
        };
    }

    private static double ResolveStepMetric(string suffix, StepStats step)
    {
        if (suffix.StartsWith("intended.", StringComparison.OrdinalIgnoreCase))
            return ResolveLatencyMetric(suffix.Substring("intended.".Length), step.Intended);
        return suffix.ToLowerInvariant() switch
        {
            "error_rate" => step.ErrorRate,
            "errors" => step.Errors,
            "count" => step.Count,
            "rps" => step.ThroughputRps,
            _ => ResolveLatencyMetric(suffix, step.Service)
        };
    }

    private static double ResolveLatencyMetric(string suffix, LatencyStats stats) => suffix.ToLowerInvariant() switch
    {
        "p50" => stats.P50Ms,
        "p75" => stats.P75Ms,
        "p90" => stats.P90Ms,
        "p95" => stats.P95Ms,
        "p99" => stats.P99Ms,
        "p999" => stats.P999Ms,
        "min" => stats.MinMs,
        "max" => stats.MaxMs,
        "mean" => stats.MeanMs,
        "stddev" => stats.StdDevMs,
        _ => throw new InvalidOperationException($"Unknown latency percentile '{suffix}'")
    };
}
