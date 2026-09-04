using LoadRunner.Core.Scenarios;
using Xunit;

namespace LoadRunner.UnitTests.Scenarios;

public class ScenarioLoaderTests
{
    [Fact]
    public void FromJson_RoundTripsConstantVUScenario()
    {
        var original = new ScenarioDefinition(
            "smoke",
            "http://localhost:5027",
            new LoadPlan(LoadModel.ConstantVUs, ConstantVUs: 4, Duration: TimeSpan.FromSeconds(2)),
            new[]
            {
                new HttpStep("Home", "GET", "/"),
            });
        var json = ScenarioLoader.ToJson(original);
        var roundtrip = ScenarioLoader.FromJson(json);
        Assert.Equal(original.Name, roundtrip.Name);
        Assert.Equal(original.Load.Model, roundtrip.Load.Model);
        Assert.Equal(original.Load.ConstantVUs, roundtrip.Load.ConstantVUs);
        Assert.Single(roundtrip.Steps);
    }

    [Fact]
    public void Validate_RejectsScenarioWithNoSteps()
    {
        var bad = new ScenarioDefinition(
            "bad",
            "http://localhost:5027",
            new LoadPlan(LoadModel.ConstantVUs, ConstantVUs: 1, Duration: TimeSpan.FromSeconds(1)),
            Array.Empty<HttpStep>());
        Assert.Throws<InvalidOperationException>(() => ScenarioLoader.Validate(bad));
    }

    [Fact]
    public void Validate_RejectsConstantArrivalRateWithoutDuration()
    {
        var bad = new ScenarioDefinition(
            "bad",
            "http://localhost:5027",
            new LoadPlan(LoadModel.ConstantArrivalRate, ConstantArrivalRatePerSec: 5),
            new[] { new HttpStep("Home", "GET", "/") });
        Assert.Throws<InvalidOperationException>(() => ScenarioLoader.Validate(bad));
    }

    [Fact]
    public void Validate_RejectsDuplicateStepNames()
    {
        var bad = new ScenarioDefinition(
            "dup",
            "http://localhost:5027",
            new LoadPlan(LoadModel.ConstantVUs, ConstantVUs: 1, Duration: TimeSpan.FromSeconds(1)),
            new[]
            {
                new HttpStep("A", "GET", "/"),
                new HttpStep("A", "GET", "/other")
            });
        Assert.Throws<InvalidOperationException>(() => ScenarioLoader.Validate(bad));
    }
}
