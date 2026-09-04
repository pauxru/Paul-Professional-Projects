using Lab.Diagnostics.Measurement;

namespace Lab.Scenarios;

public interface IIncidentScenario
{
    string Id { get; }

    string Name { get; }

    Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken);
}
