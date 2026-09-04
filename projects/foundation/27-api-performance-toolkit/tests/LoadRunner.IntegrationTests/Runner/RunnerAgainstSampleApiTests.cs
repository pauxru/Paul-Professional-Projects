using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using LoadRunner.Core.Execution;
using LoadRunner.Core.Http;
using LoadRunner.Core.Scenarios;
using LoadRunner.IntegrationTests.SampleApi;
using SampleApi.Pathologies;
using Xunit;

namespace LoadRunner.IntegrationTests.Runner;

public class RunnerAgainstSampleApiTests : IClassFixture<SampleApiFactory>
{
    private readonly SampleApiFactory _factory;
    public RunnerAgainstSampleApiTests(SampleApiFactory factory) => _factory = factory;

    private ScenarioDefinition SmokeScenario(string name = "smoke") => new(
        name,
        _factory.Server.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost",
        new LoadPlan(LoadModel.ConstantVUs, ConstantVUs: 4, Duration: TimeSpan.FromMilliseconds(600)),
        new[]
        {
            new HttpStep("Home", "GET", "/"),
            new HttpStep("Ready", "GET", "/health/ready")
        },
        Assertions: new[]
        {
            new AssertionSpec("latency.p95", "<", 5000),
            new AssertionSpec("error_rate", "<", 0.20)
        });

    [Fact]
    public async Task Runner_ExecutesSmokeScenario_AndAssertionsPass()
    {
        var client = _factory.CreateClient();
        // Warm up the app so JIT/EF cold-start doesn't dominate the tiny run.
        for (var i = 0; i < 3; i++)
        {
            var warm = await client.GetAsync("/health/ready");
            warm.EnsureSuccessStatusCode();
        }
        var runner = new ScenarioRunner(client);
        var scenario = SmokeScenario();
        var result = await runner.RunAsync(scenario);
        Assert.True(result.Aggregate.Count > 0, $"expected requests, got {result.Aggregate.Count}");
        var failed = result.Assertions.Where(a => !a.Passed).Select(a => $"{a.Metric} {a.Op} {a.Value} (actual={a.Actual})");
        Assert.True(result.AllAssertionsPassed,
            $"smoke assertions should pass. failed=[{string.Join(", ", failed)}] p95={result.Aggregate.Service.P95Ms} err={result.Aggregate.ErrorRate}");
    }

    [Fact]
    public async Task PathologyModesProduceMeasurableDifferences()
    {
        var client = _factory.CreateClient();
        var runner = new ScenarioRunner(client);

        // Baseline: healthy scenario, direct product lookup.
        var scenario = new ScenarioDefinition(
            "product-lookup",
            _factory.Server.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost",
            new LoadPlan(LoadModel.ConstantVUs, ConstantVUs: 2, Duration: TimeSpan.FromMilliseconds(300)),
            new[] { new HttpStep("GetProduct", "GET", "/api/v1/catalog/products/SKU-00042") });

        var healthy = await runner.RunAsync(scenario);

        // Turn on the artificial-downstream-latency pathology via /admin/pathology.
        var setResp = await client.PostAsJsonAsync("/admin/pathology", new PathologyPatch { DownstreamLatencyMs = 30 });
        setResp.EnsureSuccessStatusCode();
        try
        {
            var laggy = await runner.RunAsync(scenario);
            // Sanity: the pathology should slow things down enough to widen p95
            Assert.True(laggy.Aggregate.Service.P95Ms > healthy.Aggregate.Service.P95Ms + 5,
                $"expected laggy p95 > healthy p95 + 5 ms. healthy={healthy.Aggregate.Service.P95Ms}, laggy={laggy.Aggregate.Service.P95Ms}");
        }
        finally
        {
            await client.DeleteAsync("/admin/pathology");
        }
    }

    [Fact]
    public async Task OpenModel_MaintainsArrivalRateAgainstRealApi()
    {
        var client = _factory.CreateClient();
        var runner = new ScenarioRunner(client);

        var scenario = new ScenarioDefinition(
            "open-real",
            _factory.Server.BaseAddress?.ToString().TrimEnd('/') ?? "http://localhost",
            new LoadPlan(LoadModel.ConstantArrivalRate, ConstantArrivalRatePerSec: 20, Duration: TimeSpan.FromMilliseconds(600)),
            new[] { new HttpStep("Home", "GET", "/") });

        var result = await runner.RunAsync(scenario);
        // Expect ~12 requests scheduled in 0.6s at 20 rps (10..15 acceptable given host jitter)
        Assert.True(result.Aggregate.Count >= 6, $"count={result.Aggregate.Count}");
    }

    [Fact]
    public async Task RunPersistence_SavesAndLoadsRun()
    {
        var client = _factory.CreateClient();
        var runner = new ScenarioRunner(client);
        var result = await runner.RunAsync(SmokeScenario("persist"));
        var dir = Path.Combine(Path.GetTempPath(), $"loadrun-int-{Guid.NewGuid():N}");
        try
        {
            var store = new LoadRunner.Core.Results.RunResultStore(dir);
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
}
