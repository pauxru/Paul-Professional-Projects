using LoadRunner.Core.Execution;
using LoadRunner.Core.Http;
using LoadRunner.Core.LoadModels;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;
using LoadRunner.Core.Time;
using Xunit;

namespace LoadRunner.UnitTests.LoadModels;

public class ClosedModelTests
{
    [Fact]
    public async Task ConstantVUs_RunsRequestsSequentiallyPerVirtualUser()
    {
        var executor = new StubExecutor { Latency = (_, _) => TimeSpan.FromMilliseconds(50) };
        var scenario = new ScenarioDefinition(
            "closed",
            "http://stub",
            new LoadPlan(LoadModel.ConstantVUs, ConstantVUs: 3, Duration: TimeSpan.FromMilliseconds(500)),
            new[] { new HttpStep("A", "GET", "/") });
        var runner = new ScenarioRunner();
        var result = await runner.RunWithExecutorAsync(scenario, executor);
        // 3 VUs, ~50ms per request, ~500ms window => ~30 requests total
        Assert.InRange(result.Aggregate.Count, 15, 60);
        Assert.Equal(0, result.Aggregate.Errors);
    }

    [Fact]
    public async Task ClosedModel_ThroughputDegradesWhenServiceIsSlow()
    {
        var fast = new StubExecutor { Latency = (_, _) => TimeSpan.FromMilliseconds(10) };
        var slow = new StubExecutor { Latency = (_, _) => TimeSpan.FromMilliseconds(80) };
        var scenario = new ScenarioDefinition(
            "closed-slow-vs-fast",
            "http://stub",
            new LoadPlan(LoadModel.ConstantVUs, ConstantVUs: 2, Duration: TimeSpan.FromMilliseconds(400)),
            new[] { new HttpStep("A", "GET", "/") });
        var runner = new ScenarioRunner();
        var fastRes = await runner.RunWithExecutorAsync(scenario, fast);
        var slowRes = await runner.RunWithExecutorAsync(scenario, slow);
        Assert.True(fastRes.Aggregate.Count > slowRes.Aggregate.Count,
            $"fast={fastRes.Aggregate.Count}, slow={slowRes.Aggregate.Count}");
    }

    [Fact]
    public async Task RampingClosedModel_RunsStagesToCompletion()
    {
        var executor = new StubExecutor { Latency = (_, _) => TimeSpan.FromMilliseconds(10) };
        var stages = new List<LoadStage>
        {
            new(2, TimeSpan.FromMilliseconds(150)),
            new(5, TimeSpan.FromMilliseconds(150)),
            new(0, TimeSpan.FromMilliseconds(150)),
        };
        var scenario = new ScenarioDefinition(
            "ramp",
            "http://stub",
            new LoadPlan(LoadModel.RampingVUs, Stages: stages, MaxVUs: 5),
            new[] { new HttpStep("A", "GET", "/") });
        var runner = new ScenarioRunner();
        var result = await runner.RunWithExecutorAsync(scenario, executor);
        Assert.True(result.Aggregate.Count > 0);
    }
}
