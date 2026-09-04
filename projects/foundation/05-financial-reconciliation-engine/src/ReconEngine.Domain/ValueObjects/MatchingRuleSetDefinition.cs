using System.Text.Json;
using System.Text.Json.Serialization;
using ReconEngine.Domain.Normalization;

namespace ReconEngine.Domain.ValueObjects;

/// <summary>
/// The complete, data-driven definition of how a reconciliation should match. This is the payload
/// persisted (and versioned) inside a <c>MatchingRuleSet</c>; changing matching behaviour is a data
/// change, not a code change. Every field has a safe default so older serialized rulesets keep working.
/// </summary>
public sealed record MatchingRuleSetDefinition
{
    // Rule 1 — exact reference + amount + currency.
    public bool ExactReferenceEnabled { get; init; } = true;

    // Rule 2 — composite: merchant ref + amount (± tolerance) + date within N days.
    public bool CompositeEnabled { get; init; } = true;
    public int CompositeDateWindowDays { get; init; } = 2;
    public long CompositeAmountToleranceMinor { get; init; } = 0;

    // Rule 3 — fuzzy amount + date window with absolute and/or percentage tolerance.
    public bool AmountDateWindowEnabled { get; init; } = true;
    public int AmountDateWindowDays { get; init; } = 3;
    public long AmountToleranceMinor { get; init; } = 50;      // absolute tolerance (minor units)
    public decimal AmountTolerancePercent { get; init; } = 0m; // e.g. 0.5m == 0.5%

    // Rule 4 — bounded subset-sum for many-to-one / one-to-many.
    public bool ManyToOneEnabled { get; init; } = true;
    public bool OneToManyEnabled { get; init; } = true;
    public int SubsetSumMaxGroupSize { get; init; } = 4;   // cap on group cardinality (complexity guard)
    public int SubsetSumMaxCandidates { get; init; } = 20; // cap on the candidate pool searched
    public int SubsetSumDateWindowDays { get; init; } = 3;

    // Rule 5 — fee-adjusted: internal gross == external net + expected fee.
    public bool FeeAdjustedEnabled { get; init; } = true;
    public int FeeAdjustedDateWindowDays { get; init; } = 3;
    public FeeSchedule FeeSchedule { get; init; } = FeeSchedule.Default;

    // Rule 6 — refund: a negative amount pairing to an earlier capture of the same reference.
    public bool RefundEnabled { get; init; } = true;
    public int RefundDateWindowDays { get; init; } = 30;

    // Normalisation applied at ingestion and re-applied when matching.
    public ReferenceCanonicalizationRules Canonicalization { get; init; } = ReferenceCanonicalizationRules.Default;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        WriteIndented = false,
    };

    public string Serialize() => JsonSerializer.Serialize(this, JsonOptions);

    public static MatchingRuleSetDefinition Deserialize(string json) =>
        string.IsNullOrWhiteSpace(json)
            ? new MatchingRuleSetDefinition()
            : JsonSerializer.Deserialize<MatchingRuleSetDefinition>(json, JsonOptions) ?? new MatchingRuleSetDefinition();

    public static MatchingRuleSetDefinition Default { get; } = new();
}
