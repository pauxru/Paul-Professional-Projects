using System.Globalization;
using System.Text;
using System.Text.Json;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Results;
using LoadRunner.Reporting.Svg;

namespace LoadRunner.Reporting.Html;

public static class HtmlReport
{
    public static string Render(RunResult result)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"en\"><head><meta charset=\"utf-8\">");
        sb.AppendFormat(CultureInfo.InvariantCulture,
            "<title>LoadRunner report — {0}</title>", Encode(result.ScenarioName));
        sb.Append("<style>");
        sb.Append("body{font-family:system-ui,sans-serif;max-width:960px;margin:20px auto;color:#1a202c;padding:0 16px;}");
        sb.Append("h1{border-bottom:2px solid #2b6cb0;padding-bottom:6px;}");
        sb.Append("h2{margin-top:32px;color:#2b6cb0;}");
        sb.Append("table{border-collapse:collapse;width:100%;margin:12px 0;}");
        sb.Append("th,td{border:1px solid #cbd5e0;padding:6px 10px;text-align:left;font-size:14px;}");
        sb.Append("th{background:#edf2f7;}");
        sb.Append(".pass{color:#22543d;font-weight:600;}");
        sb.Append(".fail{color:#742a2a;font-weight:600;}");
        sb.Append(".chart{margin:12px 0;box-shadow:0 1px 2px rgba(0,0,0,0.08);border:1px solid #edf2f7;}");
        sb.Append(".meta{color:#4a5568;font-size:13px;}");
        sb.Append("</style></head><body>");

        sb.AppendFormat("<h1>LoadRunner report</h1>");
        sb.AppendFormat("<p class=\"meta\">Scenario: <b>{0}</b>. Run id: <code>{1}</code>. Started: {2:yyyy-MM-dd HH:mm:ss} UTC. Duration: {3:F1}s. Warmup skipped: {4}. Omitted samples: {5}.</p>",
            Encode(result.ScenarioName), Encode(result.RunId),
            result.StartedUtc.UtcDateTime,
            (result.FinishedUtc - result.StartedUtc).TotalSeconds,
            FormatTs(result.Warmup),
            result.OmittedWarmupSamples);

        sb.Append("<h2>Environment</h2><table>");
        sb.AppendFormat("<tr><th>OS</th><td>{0}</td></tr>", Encode(result.Environment.OperatingSystem));
        sb.AppendFormat("<tr><th>Cores</th><td>{0}</td></tr>", result.Environment.LogicalCores);
        sb.AppendFormat("<tr><th>Runtime</th><td>{0}</td></tr>", Encode(result.Environment.RuntimeVersion));
        sb.AppendFormat("<tr><th>Machine</th><td>{0}</td></tr>", Encode(result.Environment.MachineName));
        if (result.Environment.GitCommit is not null)
            sb.AppendFormat("<tr><th>Git commit</th><td><code>{0}</code></td></tr>", Encode(result.Environment.GitCommit));
        sb.Append("</table>");

        sb.Append("<h2>Aggregate latency</h2>");
        AppendLatencyTable(sb, result.Aggregate);

        sb.Append("<h2>Charts</h2>");
        AppendChart(sb, SvgCharts.LatencyOverTime(result.TimeSeries));
        AppendChart(sb, SvgCharts.ThroughputOverTime(result.TimeSeries));
        AppendChart(sb, SvgCharts.ErrorsOverTime(result.TimeSeries));
        AppendChart(sb, SvgCharts.PercentileDistribution(result.Aggregate.Service));

        if (result.PerStep.Count > 1)
        {
            sb.Append("<h2>Per-step results</h2>");
            foreach (var step in result.PerStep)
            {
                sb.AppendFormat("<h3>{0}</h3>", Encode(step.StepName));
                AppendLatencyTable(sb, step);
            }
        }

        if (result.Assertions.Count > 0)
        {
            sb.Append("<h2>Assertions</h2><table><tr><th>Metric</th><th>Operator</th><th>Target</th><th>Actual</th><th>Result</th></tr>");
            foreach (var a in result.Assertions)
                sb.AppendFormat("<tr><td>{0}</td><td>{1}</td><td>{2:F3}</td><td>{3:F3}</td><td class=\"{4}\">{5}</td></tr>",
                    Encode(a.Metric), Encode(a.Op), a.Value, a.Actual,
                    a.Passed ? "pass" : "fail",
                    a.Passed ? "PASS" : "FAIL");
            sb.Append("</table>");
        }

        if (result.Knee is not null)
        {
            sb.Append("<h2>Stress test</h2>");
            sb.AppendFormat("<p>Detected breaking point: <b>{0}</b> rps ({1} method). Estimated knee at <b>{2}</b> rps.</p>",
                result.Knee.BreakingPointRate?.ToString() ?? "not reached",
                Encode(result.Knee.Method),
                result.Knee.KneeRate?.ToString() ?? "unknown");
        }

        if (result.Capacity is not null)
        {
            sb.Append("<h2>Capacity search</h2>");
            sb.AppendFormat("<p>Maximum sustained rate: <b>{0}</b> rps at p95={1:F1}ms, error rate={2:P2} after {3} probes.</p>",
                result.Capacity.MaximumSustainedRate,
                result.Capacity.AchievedP95Ms,
                result.Capacity.AchievedErrorRate,
                result.Capacity.ProbesRun);
        }

        if (result.SoakDrift is not null)
        {
            sb.Append("<h2>Soak drift</h2>");
            sb.AppendFormat("<p>Latency slope: <b>{0:F2}</b> ms/min (R²={1:F2}) — {2}. Throughput slope: <b>{3:F2}</b> rps/min — {4}.</p>",
                result.SoakDrift.LatencySlopeMsPerMinute,
                result.SoakDrift.LatencyR2,
                result.SoakDrift.LatencyRegression ? "REGRESSION" : "stable",
                result.SoakDrift.ThroughputSlopeRpsPerMinute,
                result.SoakDrift.ThroughputRegression ? "REGRESSION" : "stable");
        }

        sb.Append("<h2>Scenario definition</h2><pre style=\"background:#edf2f7;padding:12px;overflow:auto;\">");
        sb.Append(Encode(JsonSerializer.Serialize(result.Scenario, RunResultStore.JsonOptions)));
        sb.Append("</pre>");
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void AppendLatencyTable(StringBuilder sb, StepStats stats)
    {
        sb.Append("<table>");
        sb.AppendFormat("<tr><th>Count</th><td>{0:N0}</td><th>Throughput (rps)</th><td>{1:F1}</td><th>Errors</th><td>{2:N0} ({3:P2})</td></tr>",
            stats.Count, stats.ThroughputRps, stats.Errors, stats.ErrorRate);
        sb.Append("<tr><th>p50</th><td>").Append(FmtMs(stats.Service.P50Ms))
            .Append("</td><th>p90</th><td>").Append(FmtMs(stats.Service.P90Ms))
            .Append("</td><th>p95</th><td>").Append(FmtMs(stats.Service.P95Ms))
            .Append("</td></tr>");
        sb.Append("<tr><th>p99</th><td>").Append(FmtMs(stats.Service.P99Ms))
            .Append("</td><th>p99.9</th><td>").Append(FmtMs(stats.Service.P999Ms))
            .Append("</td><th>max</th><td>").Append(FmtMs(stats.Service.MaxMs))
            .Append("</td></tr>");
        sb.Append("<tr><th>Intended p95</th><td>").Append(FmtMs(stats.Intended.P95Ms))
            .Append("</td><th>Intended p99</th><td>").Append(FmtMs(stats.Intended.P99Ms))
            .Append("</td><th>Intended max</th><td>").Append(FmtMs(stats.Intended.MaxMs))
            .Append("</td></tr>");
        sb.Append("<tr><th>Connection err</th><td>").Append(stats.ConnectionErrors)
            .Append("</td><th>Timeout</th><td>").Append(stats.TimeoutErrors)
            .Append("</td><th>4xx / 5xx / assertion</th><td>")
            .Append(stats.Http4xx).Append(" / ").Append(stats.Http5xx).Append(" / ").Append(stats.AssertionErrors)
            .Append("</td></tr>");
        sb.Append("</table>");
    }

    private static void AppendChart(StringBuilder sb, string svg)
    {
        sb.Append("<div class=\"chart\">").Append(svg).Append("</div>");
    }

    private static string FmtMs(double ms) => ms.ToString("F1", CultureInfo.InvariantCulture) + " ms";
    private static string FormatTs(TimeSpan ts) => ts == TimeSpan.Zero ? "-" : ts.ToString(@"hh\:mm\:ss");
    private static string Encode(string? s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);
}
