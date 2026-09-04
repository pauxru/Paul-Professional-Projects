namespace FraudPipeline.Domain.Rules;

/// <summary>
/// Fixed catalogue of rule types the engine can execute.
/// Rulesets refer to these by name; new rule types must be added deliberately.
/// </summary>
public enum RuleKind
{
    Velocity = 1,
    DistinctMerchants = 2,
    AmountSum = 3,
    UnusualAmountZScore = 4,
    FirstTimeHighValue = 5,
    NewDevice = 6,
    DeviceSharing = 7,
    IpReputation = 8,
    IpCountryMismatch = 9,
    ImpossibleTravel = 10,
    MerchantMccRisk = 11,
    TimeOfDayAnomaly = 12,
    CardTestingPattern = 13,
    RoundAmountAnomaly = 14,
    DenyList = 15,
    AllowList = 16
}

/// <summary>
/// A single declarative rule inside a ruleset.
/// Contract:
///   Kind    — which rule engine executes it
///   Weight  — score contribution when the rule fires (0..1000)
///   Params  — parameter bag (e.g. WindowSeconds=60, Threshold=5)
/// </summary>
public sealed record RuleDefinition(
    string Id,
    RuleKind Kind,
    int Weight,
    IReadOnlyDictionary<string, string> Params,
    bool Enabled = true)
{
    public T GetParam<T>(string name, T defaultValue) where T : IParsable<T>
    {
        if (!Params.TryGetValue(name, out var raw) || raw is null) return defaultValue;
        return T.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : defaultValue;
    }

    public string GetParam(string name, string defaultValue)
        => Params.TryGetValue(name, out var v) && v is not null ? v : defaultValue;
}
