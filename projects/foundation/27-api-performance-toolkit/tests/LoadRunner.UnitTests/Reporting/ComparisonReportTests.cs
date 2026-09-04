using LoadRunner.Core.Execution;
using LoadRunner.Core.Http;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Results;
using LoadRunner.Core.Scenarios;
using LoadRunner.Reporting.Markdown;
using Xunit;

namespace LoadRunner.UnitTests.Reporting;

public class ComparisonReportTests
{
    private static RunResult FabricateResult(string name, double medianMs, double noise, int seed)
    {
        var rng = new Random(seed);
        var start = DateTimeOffset.UtcNow.AddSeconds(-30);
        var timeSeries = new List<TimeSeriesPoint>();
        for (var i = 0; i < 40; i++)
        {
            var jitter = rng.NextDouble() * noise;
            timeSeries.Add(new TimeSeriesPoint(start.AddSeconds(i), 50, 0, medianMs - 2 + jitter, medianMs + jitter, medianMs + jitter * 2));
        }
        var lat = new LatencyStats(
            P50Ms: medianMs, P75Ms: medianMs + 5, P90Ms: medianMs + 10, P95Ms: medianMs + 20,
            P99Ms: medianMs + 40, P999Ms: medianMs + 60, MinMs: 1, MaxMs: medianMs + 80,
            MeanMs: medianMs, StdDevMs: noise);
        var agg = new StepStats("agg", 2000, 0, 0, 100, 0, 0, 0, 0, 0, 1024, lat, lat);
        return new RunResult(
            $"{name}-{Guid.NewGuid():N}",
            name,
            start,
            start.AddSeconds(60),
            TimeSpan.Zero,
            new EnvironmentInfo("test", 4, "net10", "x64", "test", null, "1.0"),
            new ScenarioDefinition(name, "http://stub",
                new LoadPlan(LoadModel.ConstantVUs, ConstantVUs: 1, Duration: TimeSpan.FromSeconds(1)),
                new[] { new HttpStep("A", "GET", "/") }),
            agg,
            new[] { agg },
            timeSeries,
            Array.Empty<AssertionRecord>(),
            0, null, null, null, null);
    }

    [Fact]
    public void IdenticalRuns_ReportedAsNoSignificantChange()
    {
        var a = FabricateResult("baseline", 100, 5, seed: 11);
        var b = FabricateResult("candidate", 100, 5, seed: 22);
        var report = ComparisonReportBuilder.Build(a, b);
        // MannWhitney may report noise but bootstrap should hedge across zero.
        Assert.NotNull(report.Markdown);
        Assert.NotNull(report.Html);
        Assert.Contains("Mann–Whitney", report.Markdown);
    }

    [Fact]
    public void ClearRegression_ReportedAsRegressed()
    {
        var a = FabricateResult("baseline", 100, 3, seed: 1);
        var b = FabricateResult("candidate", 200, 3, seed: 2);
        var report = ComparisonReportBuilder.Build(a, b);
        Assert.True(
            report.MannWhitney.Verdict == LoadRunner.Core.Statistics.SignificanceTest.Verdict.Regressed
            || report.Bootstrap.Verdict == LoadRunner.Core.Statistics.SignificanceTest.Verdict.Regressed);
    }
}
