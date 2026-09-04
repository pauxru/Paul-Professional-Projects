using LoadRunner.Core.Execution;
using LoadRunner.Core.Http;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Results;
using LoadRunner.Core.Scenarios;
using LoadRunner.Reporting.Html;
using LoadRunner.Reporting.Markdown;
using LoadRunner.Reporting.Svg;
using Xunit;

namespace LoadRunner.UnitTests.Reporting;

public class ReportGenerationTests
{
    private static RunResult MakeResult()
    {
        var executor = new StubExecutor
        {
            Latency = (_, vu) => TimeSpan.FromMilliseconds(5 + (vu % 3) * 2)
        };
        var scenario = new ScenarioDefinition(
            "smoke",
            "http://stub",
            new LoadPlan(LoadModel.ConstantVUs, ConstantVUs: 2, Duration: TimeSpan.FromMilliseconds(300)),
            new[] { new HttpStep("A", "GET", "/") },
            Assertions: new[] { new AssertionSpec("latency.p95", "<", 500), new AssertionSpec("error_rate", "<", 0.02) });
        var runner = new ScenarioRunner();
        return runner.RunWithExecutorAsync(scenario, executor).Result;
    }

    [Fact]
    public void HtmlReport_ContainsScenarioNameAndSvgChart()
    {
        var result = MakeResult();
        var html = HtmlReport.Render(result);
        Assert.Contains("smoke", html);
        Assert.Contains("<svg", html);
        Assert.Contains("</html>", html);
    }

    [Fact]
    public void MarkdownReport_ContainsHeaderAndAggregateTable()
    {
        var result = MakeResult();
        var md = MarkdownReport.Render(result);
        Assert.Contains("# LoadRunner report", md);
        Assert.Contains("Throughput", md);
        Assert.Contains("p50", md);
    }

    [Fact]
    public void JsonReport_RoundTripsThroughStore()
    {
        var result = MakeResult();
        var dir = Path.Combine(Path.GetTempPath(), $"loadrun-test-{Guid.NewGuid():N}");
        try
        {
            var store = new RunResultStore(dir);
            var path = store.Save(result);
            Assert.True(File.Exists(path));
            var loaded = store.Load(path);
            Assert.Equal(result.RunId, loaded.RunId);
            Assert.Equal(result.Aggregate.Count, loaded.Aggregate.Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SvgLatencyOverTime_ProducesValidXml()
    {
        var result = MakeResult();
        var svg = SvgCharts.LatencyOverTime(result.TimeSeries);
        Assert.StartsWith("<svg", svg);
        Assert.EndsWith("</svg>", svg);
    }
}
