using Lab.Diagnostics.Measurement;
using Lab.Scenarios.Incidents;

namespace Lab.Scenarios;

public sealed class ScenarioCatalog
{
    private readonly IReadOnlyDictionary<string, IIncidentScenario> _scenarios;

    public ScenarioCatalog()
    {
        var scenarios = new IIncidentScenario[]
        {
            new NPlusOneScenario(),
            new MissingIndexScenario(),
            new ConnectionPoolExhaustionScenario(),
            new MemoryLeakScenario(),
            new BlockingAsyncScenario(),
            new ThreadPoolStarvationScenario(),
            new DownstreamTimeoutScenario(),
            new RetryStormScenario(),
            new PoisonQueueScenario(),
            new CacheStampedeScenario()
        };

        _scenarios = scenarios.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyCollection<IIncidentScenario> All => [.. _scenarios.Values];

    public IIncidentScenario GetRequired(string scenarioId)
    {
        if (!_scenarios.TryGetValue(scenarioId, out var scenario))
        {
            throw new ArgumentException($"Unknown scenario '{scenarioId}'. Valid IDs: {string.Join(", ", _scenarios.Keys)}.", nameof(scenarioId));
        }

        return scenario;
    }

    public Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken) =>
        GetRequired(options.ScenarioId).RunAsync(options, cancellationToken);
}
