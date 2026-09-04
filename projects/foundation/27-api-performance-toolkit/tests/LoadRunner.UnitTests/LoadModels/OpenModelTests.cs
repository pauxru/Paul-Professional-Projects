using LoadRunner.Core.Execution;
using LoadRunner.Core.Http;
using LoadRunner.Core.LoadModels;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;
using Xunit;

namespace LoadRunner.UnitTests.LoadModels;

public class OpenModelTests
{
    [Fact]
    public async Task OpenModel_MaintainsTargetArrivalRate_UnderFastServer()
    {
        var executor = new StubExecutor { Latency = (_, _) => TimeSpan.FromMilliseconds(5) };
        var scenario = new ScenarioDefinition(
            "open-fast",
            "http://stub",
            new LoadPlan(LoadModel.ConstantArrivalRate, ConstantArrivalRatePerSec: 100, Duration: TimeSpan.FromMilliseconds(1000)),
            new[] { new HttpStep("A", "GET", "/") });
        var runner = new ScenarioRunner();
        var result = await runner.RunWithExecutorAsync(scenario, executor);
        // Should be ~100 requests (allow slack for scheduling)
        Assert.InRange(result.Aggregate.Count, 70, 110);
    }

    [Fact]
    public async Task OpenModel_UnderSlowServer_RecordsIntendedLatencyDrift()
    {
        // Slow server (80ms) with 50 rps: the runner MUST still schedule ~50 requests/sec
        // (this is the coordinated-omission avoidance), and intended latencies must reflect the queue.
        var executor = new StubExecutor { Latency = (_, _) => TimeSpan.FromMilliseconds(80) };
        var scenario = new ScenarioDefinition(
            "open-slow",
            "http://stub",
            new LoadPlan(LoadModel.ConstantArrivalRate, ConstantArrivalRatePerSec: 50, Duration: TimeSpan.FromMilliseconds(1500)),
            new[] { new HttpStep("A", "GET", "/") });
        var runner = new ScenarioRunner();
        var result = await runner.RunWithExecutorAsync(scenario, executor);
        // We should have scheduled ~75 requests, not <10 as a closed-model tester would deliver
        Assert.True(result.Aggregate.Count >= 40, $"count={result.Aggregate.Count}");
        // Intended latency should be at least as large as service latency; on average larger
        Assert.True(result.Aggregate.Intended.P95Ms >= result.Aggregate.Service.P95Ms,
            $"intended p95={result.Aggregate.Intended.P95Ms}, service p95={result.Aggregate.Service.P95Ms}");
    }

    [Fact]
    public async Task OpenModel_ScheduledCount_ScalesWithRate()
    {
        var executor = new StubExecutor { Latency = (_, _) => TimeSpan.FromMilliseconds(2) };
        var scenario50 = new ScenarioDefinition(
            "50rps", "http://stub",
            new LoadPlan(LoadModel.ConstantArrivalRate, ConstantArrivalRatePerSec: 50, Duration: TimeSpan.FromMilliseconds(600)),
            new[] { new HttpStep("A", "GET", "/") });
        var scenario200 = scenario50 with
        {
            Name = "200rps",
            Load = scenario50.Load with { ConstantArrivalRatePerSec = 200 }
        };
        var runner = new ScenarioRunner();
        var r50 = await runner.RunWithExecutorAsync(scenario50, executor);
        var r200 = await runner.RunWithExecutorAsync(scenario200, executor);
        Assert.True(r200.Aggregate.Count > r50.Aggregate.Count * 2.5,
            $"50rps count={r50.Aggregate.Count}, 200rps count={r200.Aggregate.Count}");
    }
}
