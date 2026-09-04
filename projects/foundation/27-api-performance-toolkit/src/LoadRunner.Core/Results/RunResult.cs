using System.Text.Json.Serialization;
using LoadRunner.Core.Analysis;
using LoadRunner.Core.Assertions;
using LoadRunner.Core.Metrics;
using LoadRunner.Core.Scenarios;

namespace LoadRunner.Core.Results;

public sealed record EnvironmentInfo(
    string OperatingSystem,
    int LogicalCores,
    string RuntimeVersion,
    string ProcessArchitecture,
    string MachineName,
    string? GitCommit,
    string ToolkitVersion);

public sealed record AssertionRecord(string Metric, string Op, double Value, double Actual, bool Passed);

public sealed record RunResult(
    string RunId,
    string ScenarioName,
    DateTimeOffset StartedUtc,
    DateTimeOffset FinishedUtc,
    TimeSpan Warmup,
    EnvironmentInfo Environment,
    ScenarioDefinition Scenario,
    StepStats Aggregate,
    IReadOnlyList<StepStats> PerStep,
    IReadOnlyList<TimeSeriesPoint> TimeSeries,
    IReadOnlyList<AssertionRecord> Assertions,
    long OmittedWarmupSamples,
    SoakDrift? SoakDrift,
    CapacityBinarySearch.CapacityResult? Capacity,
    IReadOnlyList<StressStepResult>? StressSteps,
    KneeDetector.KneeResult? Knee)
{
    [JsonIgnore]
    public bool AllAssertionsPassed => Assertions.All(a => a.Passed);
}
