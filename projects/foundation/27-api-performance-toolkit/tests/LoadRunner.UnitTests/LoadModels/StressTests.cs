using LoadRunner.Core.Execution;
using LoadRunner.Core.Http;
using LoadRunner.Core.Scenarios;
using Xunit;

namespace LoadRunner.UnitTests.LoadModels;

public class StressTests
{
    [Fact]
    public async Task StressModel_ProducesStepByStepResults_UpToMaxRate()
    {
        var executor = new StubExecutor
        {
            Latency = (_, vu) => TimeSpan.FromMilliseconds(5)
        };
        var scenario = new ScenarioDefinition(
            "stress",
            "http://stub",
            new LoadPlan(LoadModel.Stress,
                StressStartRate: 20,
                StressStepRate: 20,
                StressMaxRate: 80,
                StressStepDuration: TimeSpan.FromMilliseconds(150)),
            new[] { new HttpStep("A", "GET", "/") });
        var runner = new ScenarioRunner();
        var result = await runner.RunWithExecutorAsync(scenario, executor);
        Assert.NotNull(result.StressSteps);
        Assert.True(result.StressSteps!.Count >= 3);
        Assert.NotNull(result.Knee);
    }
}
