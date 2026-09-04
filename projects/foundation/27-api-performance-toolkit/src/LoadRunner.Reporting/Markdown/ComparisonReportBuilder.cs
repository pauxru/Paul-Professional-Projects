using System.Text;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Results;
using LoadRunner.Core.Statistics;
using LoadRunner.Reporting.Svg;

namespace LoadRunner.Reporting.Markdown;

public sealed record ComparisonReport(
    RunResult Baseline,
    RunResult Candidate,
    SignificanceTest.MannWhitneyResult MannWhitney,
    SignificanceTest.BootstrapResult Bootstrap,
    string Markdown,
    string Html);

public static class ComparisonReportBuilder
{
    public static ComparisonReport Build(RunResult baseline, RunResult candidate)
    {
        var aLat = ExtractLatencySamples(baseline);
        var bLat = ExtractLatencySamples(candidate);
        var mwu = SignificanceTest.MannWhitneyU(aLat, bLat);
        var boot = SignificanceTest.BootstrapMedianDiff(aLat, bLat, iterations: 1500, seed: 42);

        var md = BuildMarkdown(baseline, candidate, mwu, boot);
        var html = BuildHtml(baseline, candidate, mwu, boot);
        return new ComparisonReport(baseline, candidate, mwu, boot, md, html);
    }

    private static double[] ExtractLatencySamples(RunResult r)
    {
        // Prefer time-series p95 samples if any; otherwise expand from percentile stats.
        if (r.TimeSeries.Count > 0)
            return r.TimeSeries.Select(p => p.P95Ms).Where(v => v > 0).ToArray();
        return [r.Aggregate.Service.P50Ms, r.Aggregate.Service.P90Ms, r.Aggregate.Service.P95Ms, r.Aggregate.Service.P99Ms];
    }

    private static string BuildMarkdown(RunResult a, RunResult b, SignificanceTest.MannWhitneyResult mwu, SignificanceTest.BootstrapResult boot)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Comparison — {a.ScenarioName} vs {b.ScenarioName}");
        sb.AppendLine();
        sb.AppendLine($"Baseline run: `{a.RunId}` — {a.StartedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"Candidate run: `{b.RunId}` — {b.StartedUtc:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine("| Metric | Baseline | Candidate | Delta | % change |");
        sb.AppendLine("|---|---|---|---|---|");
        Row(sb, "p50 (ms)", a.Aggregate.Service.P50Ms, b.Aggregate.Service.P50Ms);
        Row(sb, "p95 (ms)", a.Aggregate.Service.P95Ms, b.Aggregate.Service.P95Ms);
        Row(sb, "p99 (ms)", a.Aggregate.Service.P99Ms, b.Aggregate.Service.P99Ms);
        Row(sb, "intended p95 (ms)", a.Aggregate.Intended.P95Ms, b.Aggregate.Intended.P95Ms);
        Row(sb, "throughput (rps)", a.Aggregate.ThroughputRps, b.Aggregate.ThroughputRps);
        Row(sb, "error rate", a.Aggregate.ErrorRate, b.Aggregate.ErrorRate, isRate: true);
        sb.AppendLine();

        sb.AppendLine("## Statistical significance");
        sb.AppendLine();
        sb.AppendLine("**Mann–Whitney U (two-sided, tie-corrected normal approximation)**");
        sb.AppendLine();
        sb.AppendLine($"- U1 = {mwu.U1:F2}, U2 = {mwu.U2:F2}");
        sb.AppendLine($"- z-score = {mwu.ZScore:F3}");
        sb.AppendLine($"- p-value = {mwu.PValue:F4}");
        sb.AppendLine($"- Verdict: **{mwu.Verdict}**");
        sb.AppendLine();
        sb.AppendLine("**Bootstrap CI on median difference (candidate minus baseline)**");
        sb.AppendLine();
        sb.AppendLine($"- median baseline = {boot.MedianA:F2}");
        sb.AppendLine($"- median candidate = {boot.MedianB:F2}");
        sb.AppendLine($"- median delta = {boot.MedianDelta:F2}");
        sb.AppendLine($"- {boot.Confidence:P0} CI = [{boot.CiLow:F2}, {boot.CiHigh:F2}]");
        sb.AppendLine($"- Verdict: **{boot.Verdict}**");
        sb.AppendLine();

        var combined = mwu.Verdict == boot.Verdict
            ? mwu.Verdict
            : mwu.Verdict == SignificanceTest.Verdict.NoSignificantChange
                ? boot.Verdict
                : mwu.Verdict;
        sb.AppendLine("## Overall verdict");
        sb.AppendLine();
        sb.AppendLine($"**{combined}** — both tests {(mwu.Verdict == boot.Verdict ? "agree" : "differ; primary verdict taken from Mann–Whitney U")}.");
        return sb.ToString();
    }

    private static string BuildHtml(RunResult a, RunResult b, SignificanceTest.MannWhitneyResult mwu, SignificanceTest.BootstrapResult boot)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Comparison</title>");
        sb.Append("<style>body{font-family:system-ui,sans-serif;max-width:960px;margin:20px auto;padding:0 16px;color:#1a202c;}table{border-collapse:collapse;width:100%;margin:12px 0;}th,td{border:1px solid #cbd5e0;padding:6px 10px;}th{background:#edf2f7;}.pass{color:#22543d;}.fail{color:#742a2a;}</style>");
        sb.Append("</head><body>");
        sb.AppendFormat("<h1>Comparison — {0} vs {1}</h1>",
            System.Net.WebUtility.HtmlEncode(a.ScenarioName),
            System.Net.WebUtility.HtmlEncode(b.ScenarioName));
        sb.Append("<h2>p95 latency overlay</h2>");
        sb.Append(SvgCharts.ComparisonOverlay(a.TimeSeries, b.TimeSeries));
        sb.Append("<h2>Metrics</h2><table><tr><th>Metric</th><th>Baseline</th><th>Candidate</th><th>Δ</th><th>%</th></tr>");
        AppendRow(sb, "p50 (ms)", a.Aggregate.Service.P50Ms, b.Aggregate.Service.P50Ms);
        AppendRow(sb, "p95 (ms)", a.Aggregate.Service.P95Ms, b.Aggregate.Service.P95Ms);
        AppendRow(sb, "p99 (ms)", a.Aggregate.Service.P99Ms, b.Aggregate.Service.P99Ms);
        AppendRow(sb, "throughput (rps)", a.Aggregate.ThroughputRps, b.Aggregate.ThroughputRps);
        AppendRow(sb, "error rate", a.Aggregate.ErrorRate, b.Aggregate.ErrorRate, true);
        sb.Append("</table>");
        sb.Append("<h2>Significance</h2>");
        sb.AppendFormat("<p>Mann–Whitney U: p={0:F4}, verdict: <b>{1}</b>.</p>", mwu.PValue, mwu.Verdict);
        sb.AppendFormat("<p>Bootstrap median diff: {0:F2} ({1:F2}, {2:F2}) at {3:P0} confidence, verdict: <b>{4}</b>.</p>",
            boot.MedianDelta, boot.CiLow, boot.CiHigh, boot.Confidence, boot.Verdict);
        sb.Append("</body></html>");
        return sb.ToString();
    }

    private static void Row(StringBuilder sb, string label, double baseline, double candidate, bool isRate = false)
    {
        var delta = candidate - baseline;
        var pct = baseline == 0 ? 0 : (delta / baseline) * 100.0;
        var format = isRate ? "P2" : "F2";
        sb.AppendLine($"| {label} | {baseline.ToString(format)} | {candidate.ToString(format)} | {delta.ToString(format)} | {pct:F1}% |");
    }

    private static void AppendRow(StringBuilder sb, string label, double baseline, double candidate, bool isRate = false)
    {
        var delta = candidate - baseline;
        var pct = baseline == 0 ? 0 : (delta / baseline) * 100.0;
        var format = isRate ? "P2" : "F2";
        sb.AppendFormat("<tr><td>{0}</td><td>{1}</td><td>{2}</td><td>{3}</td><td>{4:F1}%</td></tr>",
            System.Net.WebUtility.HtmlEncode(label),
            baseline.ToString(format),
            candidate.ToString(format),
            delta.ToString(format),
            pct);
    }
}
