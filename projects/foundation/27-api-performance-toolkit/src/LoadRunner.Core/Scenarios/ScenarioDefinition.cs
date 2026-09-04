namespace LoadRunner.Core.Scenarios;

public enum LoadModel
{
    ConstantVUs,
    RampingVUs,
    ConstantArrivalRate,
    RampingArrivalRate,
    Stress,
    Spike,
    Soak,
    CapacitySearch
}

public enum ThinkTimeDistribution
{
    None,
    Constant,
    Uniform,
    Normal,
    Exponential
}

public sealed record ThinkTimeSpec(
    ThinkTimeDistribution Distribution = ThinkTimeDistribution.None,
    double Mean = 0,
    double StdDev = 0,
    double Min = 0,
    double Max = 0);

public sealed record LoadStage(int TargetLoad, TimeSpan Duration);

public sealed record LoadPlan(
    LoadModel Model,
    int? ConstantVUs = null,
    int? ConstantArrivalRatePerSec = null,
    TimeSpan? Duration = null,
    TimeSpan? WarmUp = null,
    IReadOnlyList<LoadStage>? Stages = null,
    int? MaxVUs = null,
    int? StressStartRate = null,
    int? StressStepRate = null,
    TimeSpan? StressStepDuration = null,
    int? StressMaxRate = null,
    double? StressKneeP95Ms = null,
    double? StressKneeErrorRate = null,
    int? SpikeBaseVUs = null,
    int? SpikePeakVUs = null,
    TimeSpan? SpikeHoldDuration = null,
    int? CapacityMinRate = null,
    int? CapacityMaxRate = null,
    double? CapacityP95TargetMs = null,
    TimeSpan? CapacityRunDuration = null);

public sealed record HttpStep(
    string Name,
    string Method,
    string Url,
    Dictionary<string, string>? Headers = null,
    string? Body = null,
    Dictionary<string, string>? ExtractFromJson = null,
    Dictionary<string, string>? ExtractFromHeader = null,
    ThinkTimeSpec? ThinkTime = null,
    int ExpectedStatus = 200,
    double? Weight = null);

public sealed record CsvFeederSpec(
    string Path,
    string Variable,
    bool Cycle = true);

public sealed record AssertionSpec(
    string Metric,
    string Op,
    double Value);

public sealed record ScenarioDefinition(
    string Name,
    string BaseUrl,
    LoadPlan Load,
    IReadOnlyList<HttpStep> Steps,
    Dictionary<string, string>? Variables = null,
    CsvFeederSpec? Feeder = null,
    IReadOnlyList<AssertionSpec>? Assertions = null,
    Dictionary<string, string>? Environment = null);

public sealed record ScenarioResultVerdict(bool Success, IReadOnlyList<string> Reasons);
