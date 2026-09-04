using System.Globalization;
using System.Text;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Results;

namespace LoadRunner.Reporting.Markdown;

public static class MarkdownReport
{
    public static string Render(RunResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# LoadRunner report — {result.ScenarioName}");
        sb.AppendLine();
        sb.AppendLine($"- Run id: `{result.RunId}`");
        sb.AppendLine($"- Started (UTC): {result.StartedUtc:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"- Duration: {(result.FinishedUtc - result.StartedUtc).TotalSeconds:F1}s");
        sb.AppendLine($"- Warmup skipped: {result.Warmup} ({result.OmittedWarmupSamples} samples)");
        sb.AppendLine($"- Runtime: {result.Environment.RuntimeVersion}");
        sb.AppendLine($"- Host: {result.Environment.MachineName} ({result.Environment.OperatingSystem}, {result.Environment.LogicalCores} cores)");
        if (result.Environment.GitCommit is not null)
            sb.AppendLine($"- Git commit: `{result.Environment.GitCommit}`");
        sb.AppendLine();

        sb.AppendLine("## Aggregate");
        AppendStats(sb, result.Aggregate);

        if (result.PerStep.Count > 1)
        {
            sb.AppendLine();
            sb.AppendLine("## Per-step results");
            foreach (var step in result.PerStep)
            {
                sb.AppendLine();
                sb.AppendLine($"### {step.StepName}");
                AppendStats(sb, step);
            }
        }

        if (result.Assertions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Assertions");
            sb.AppendLine("| Metric | Op | Target | Actual | Result |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var a in result.Assertions)
                sb.AppendLine($"| `{a.Metric}` | `{a.Op}` | {a.Value:F3} | {a.Actual:F3} | **{(a.Passed ? "PASS" : "FAIL")}** |");
        }

        if (result.StressSteps is not null && result.StressSteps.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Stress test");
            sb.AppendLine("| Rate (rps) | p95 (ms) | Error rate | Count |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var s in result.StressSteps)
                sb.AppendLine($"| {s.RatePerSec} | {s.P95Ms:F1} | {s.ErrorRate:P2} | {s.Count} |");
            if (result.Knee is not null)
            {
                sb.AppendLine();
                sb.AppendLine($"Breaking point: **{result.Knee.BreakingPointRate?.ToString() ?? "not reached"}** rps ({result.Knee.Method}). Estimated knee: **{result.Knee.KneeRate?.ToString() ?? "unknown"}** rps.");
            }
        }

        if (result.Capacity is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Capacity search");
            sb.AppendLine($"Maximum sustained rate: **{result.Capacity.MaximumSustainedRate}** rps (p95={result.Capacity.AchievedP95Ms:F1}ms, err={result.Capacity.AchievedErrorRate:P2}, {result.Capacity.ProbesRun} probes).");
        }

        if (result.SoakDrift is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Soak drift");
            sb.AppendLine($"Latency slope: **{result.SoakDrift.LatencySlopeMsPerMinute:F2}** ms/min (R²={result.SoakDrift.LatencyR2:F2}) — {(result.SoakDrift.LatencyRegression ? "**REGRESSION**" : "stable")}.");
            sb.AppendLine($"Throughput slope: **{result.SoakDrift.ThroughputSlopeRpsPerMinute:F2}** rps/min — {(result.SoakDrift.ThroughputRegression ? "**REGRESSION**" : "stable")}.");
        }

        return sb.ToString();
    }

    private static void AppendStats(StringBuilder sb, StepStats stats)
    {
        sb.AppendLine();
        sb.AppendLine("| Metric | Value |");
        sb.AppendLine("|---|---|");
        sb.AppendLine($"| Count | {stats.Count:N0} |");
        sb.AppendLine($"| Throughput | {stats.ThroughputRps.ToString("F1", CultureInfo.InvariantCulture)} rps |");
        sb.AppendLine($"| Errors | {stats.Errors:N0} ({stats.ErrorRate:P2}) |");
        sb.AppendLine($"| Latency p50 / p95 / p99 | {stats.Service.P50Ms:F1} / {stats.Service.P95Ms:F1} / {stats.Service.P99Ms:F1} ms |");
        sb.AppendLine($"| Latency p99.9 / max | {stats.Service.P999Ms:F1} / {stats.Service.MaxMs:F1} ms |");
        sb.AppendLine($"| Intended p95 / p99 / max | {stats.Intended.P95Ms:F1} / {stats.Intended.P99Ms:F1} / {stats.Intended.MaxMs:F1} ms |");
        sb.AppendLine($"| Errors — conn / timeout / 4xx / 5xx / assertion | {stats.ConnectionErrors} / {stats.TimeoutErrors} / {stats.Http4xx} / {stats.Http5xx} / {stats.AssertionErrors} |");
    }
}
