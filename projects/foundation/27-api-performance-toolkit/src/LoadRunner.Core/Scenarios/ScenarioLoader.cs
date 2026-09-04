using System.Text.Json;
using System.Text.Json.Serialization;

namespace LoadRunner.Core.Scenarios;

public static class ScenarioLoader
{
    public static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static ScenarioDefinition FromJson(string json)
    {
        var scenario = JsonSerializer.Deserialize<ScenarioDefinition>(json, JsonOptions)
            ?? throw new InvalidOperationException("Scenario JSON deserialised to null");
        Validate(scenario);
        return scenario;
    }

    public static ScenarioDefinition FromFile(string path)
    {
        var json = File.ReadAllText(path);
        return FromJson(json);
    }

    public static string ToJson(ScenarioDefinition scenario)
        => JsonSerializer.Serialize(scenario, JsonOptions);

    public static void Validate(ScenarioDefinition scenario)
    {
        if (string.IsNullOrWhiteSpace(scenario.Name))
            throw new InvalidOperationException("Scenario.Name is required");
        if (string.IsNullOrWhiteSpace(scenario.BaseUrl))
            throw new InvalidOperationException("Scenario.BaseUrl is required");
        if (scenario.Steps.Count == 0)
            throw new InvalidOperationException("Scenario.Steps must contain at least one step");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in scenario.Steps)
        {
            if (string.IsNullOrWhiteSpace(step.Name))
                throw new InvalidOperationException("Step.Name is required");
            if (!seen.Add(step.Name))
                throw new InvalidOperationException($"Duplicate step name '{step.Name}'");
            if (string.IsNullOrWhiteSpace(step.Method))
                throw new InvalidOperationException($"Step '{step.Name}' has no Method");
            if (string.IsNullOrWhiteSpace(step.Url))
                throw new InvalidOperationException($"Step '{step.Name}' has no Url");
        }

        switch (scenario.Load.Model)
        {
            case LoadModel.ConstantVUs:
                if ((scenario.Load.ConstantVUs ?? 0) <= 0)
                    throw new InvalidOperationException("ConstantVUs must be > 0");
                if (scenario.Load.Duration is null)
                    throw new InvalidOperationException("Duration is required");
                break;
            case LoadModel.ConstantArrivalRate:
                if ((scenario.Load.ConstantArrivalRatePerSec ?? 0) <= 0)
                    throw new InvalidOperationException("ConstantArrivalRatePerSec must be > 0");
                if (scenario.Load.Duration is null)
                    throw new InvalidOperationException("Duration is required");
                break;
            case LoadModel.RampingVUs:
            case LoadModel.RampingArrivalRate:
                if (scenario.Load.Stages is null || scenario.Load.Stages.Count == 0)
                    throw new InvalidOperationException("Stages required for ramping load");
                break;
            case LoadModel.Stress:
                if ((scenario.Load.StressStartRate ?? 0) <= 0 || (scenario.Load.StressStepRate ?? 0) <= 0)
                    throw new InvalidOperationException("Stress requires StressStartRate and StressStepRate");
                break;
            case LoadModel.Spike:
                if ((scenario.Load.SpikeBaseVUs ?? 0) <= 0 || (scenario.Load.SpikePeakVUs ?? 0) <= 0)
                    throw new InvalidOperationException("Spike requires SpikeBaseVUs and SpikePeakVUs");
                break;
            case LoadModel.Soak:
                if (scenario.Load.Duration is null)
                    throw new InvalidOperationException("Soak requires Duration");
                break;
            case LoadModel.CapacitySearch:
                if ((scenario.Load.CapacityMinRate ?? 0) <= 0 || (scenario.Load.CapacityMaxRate ?? 0) <= 0)
                    throw new InvalidOperationException("CapacitySearch requires min/max rate");
                if ((scenario.Load.CapacityP95TargetMs ?? 0) <= 0)
                    throw new InvalidOperationException("CapacitySearch requires CapacityP95TargetMs");
                break;
        }
    }
}
