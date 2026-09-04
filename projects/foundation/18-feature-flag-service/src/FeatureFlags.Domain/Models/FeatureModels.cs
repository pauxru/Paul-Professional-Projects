using System.Text.Json;

namespace FeatureFlags.Domain;

public enum FlagValueType { Boolean, String, Number, Json }
public enum FlagLifecycleStatus { New, Active, Deprecated, Archived }
public enum ClauseOperator
{
    Equals, NotEquals, In, NotIn, Contains, StartsWith, EndsWith, MatchesRegex,
    GreaterThan, LessThan, Before, After, SemVerEqual, SemVerGreater
}
public enum EvaluationReasonKind { Off, TargetMatch, SegmentMatch, RuleMatch, Fallthrough, PrerequisiteFailed, Error }
public enum EvaluationErrorKind { ClientNotReady, FlagNotFound, TypeMismatch, InvalidConfiguration, PrerequisiteCycle }

public static class FeatureFlagJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static JsonElement Element(object? value) => JsonSerializer.SerializeToElement(value, Options);
    public static JsonElement Null => Element(null);
}

public sealed record FlagVariation(int Index, string Name, JsonElement Value)
{
    public static FlagVariation Create(int index, string name, object? value) => new(index, name, FeatureFlagJson.Element(value));
}

public sealed record IndividualTarget(int VariationIndex, IReadOnlyList<string> ContextKeys);
public sealed record SegmentTarget(int VariationIndex, string SegmentKey);
public sealed record WeightedVariation(int VariationIndex, int WeightBps);
public sealed record PercentageRollout(IReadOnlyList<WeightedVariation> Variations);
public sealed record FlagPrerequisite(string FlagKey, int VariationIndex);

public sealed record RuleClause
{
    public string Attribute { get; init; } = string.Empty;
    public ClauseOperator Operator { get; init; }
    public JsonElement? Value { get; init; }
    public IReadOnlyList<JsonElement> Values { get; init; } = Array.Empty<JsonElement>();

    public static RuleClause ForEquals(string attribute, object? value) => new()
    {
        Attribute = attribute,
        Operator = ClauseOperator.Equals,
        Value = FeatureFlagJson.Element(value)
    };

    public static RuleClause For(string attribute, ClauseOperator @operator, object? value) => new()
    {
        Attribute = attribute,
        Operator = @operator,
        Value = FeatureFlagJson.Element(value)
    };

    public static RuleClause ForMany(string attribute, ClauseOperator @operator, params object?[] values) => new()
    {
        Attribute = attribute,
        Operator = @operator,
        Values = values.Select(FeatureFlagJson.Element).ToArray()
    };
}

public sealed record TargetingRule
{
    public string Name { get; init; } = string.Empty;
    public int VariationIndex { get; init; }
    public IReadOnlyList<RuleClause> Clauses { get; init; } = Array.Empty<RuleClause>();
}

public sealed record SegmentDefinition
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<string> IncludedContextKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedContextKeys { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> IncludedSegments { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExcludedSegments { get; init; } = Array.Empty<string>();
    public IReadOnlyList<TargetingRule> Rules { get; init; } = Array.Empty<TargetingRule>();
}

public sealed record FlagDefinition
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public FlagValueType ValueType { get; init; }
    public bool IsOn { get; init; } = true;
    public bool ClientSide { get; init; }
    public int OffVariation { get; init; }
    public int FallthroughVariation { get; init; }
    public JsonElement? FallbackValue { get; init; }
    public string Salt { get; init; } = "default-salt";
    public FlagLifecycleStatus LifecycleStatus { get; init; } = FlagLifecycleStatus.New;
    public DateTimeOffset? ActivateAt { get; init; }
    public DateTimeOffset? DeactivateAt { get; init; }
    public DateTimeOffset? TemporaryUntil { get; init; }
    public DateTimeOffset? RemovalDueAt { get; init; }
    public IReadOnlyList<FlagVariation> Variations { get; init; } = Array.Empty<FlagVariation>();
    public IReadOnlyList<IndividualTarget> Targets { get; init; } = Array.Empty<IndividualTarget>();
    public IReadOnlyList<SegmentTarget> SegmentTargets { get; init; } = Array.Empty<SegmentTarget>();
    public IReadOnlyList<TargetingRule> Rules { get; init; } = Array.Empty<TargetingRule>();
    public PercentageRollout? Rollout { get; init; }
    public IReadOnlyList<FlagPrerequisite> Prerequisites { get; init; } = Array.Empty<FlagPrerequisite>();

    public bool IsEffectiveOn(DateTimeOffset now) => IsOn
        && LifecycleStatus != FlagLifecycleStatus.Archived
        && (!ActivateAt.HasValue || now >= ActivateAt.Value)
        && (!DeactivateAt.HasValue || now < DeactivateAt.Value)
        && (!TemporaryUntil.HasValue || now < TemporaryUntil.Value);

    public JsonElement GetFallbackValue()
    {
        if (FallbackValue.HasValue)
        {
            return FallbackValue.Value;
        }

        return ValueType switch
        {
            FlagValueType.Boolean => FeatureFlagJson.Element(false),
            FlagValueType.String => FeatureFlagJson.Element(string.Empty),
            FlagValueType.Number => FeatureFlagJson.Element(0m),
            _ => FeatureFlagJson.Element(new { })
        };
    }

    public bool TryGetVariation(int index, out FlagVariation variation)
    {
        variation = Variations.FirstOrDefault(v => v.Index == index)!;
        return variation is not null;
    }
}

public sealed record EnvironmentConfiguration
{
    public string ProjectKey { get; init; } = string.Empty;
    public string EnvironmentKey { get; init; } = string.Empty;
    public long Version { get; init; }
    public DateTimeOffset GeneratedAt { get; init; }
    public IReadOnlyList<FlagDefinition> Flags { get; init; } = Array.Empty<FlagDefinition>();
    public IReadOnlyList<SegmentDefinition> Segments { get; init; } = Array.Empty<SegmentDefinition>();

    public FlagDefinition? FindFlag(string key) => Flags.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.Ordinal));
    public SegmentDefinition? FindSegment(string key) => Segments.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.Ordinal));

    public EnvironmentConfiguration ForClient() => this with { Flags = Flags.Where(f => f.ClientSide).ToArray() };
}

public sealed class EvaluationContext
{
    public EvaluationContext(
        string key,
        string kind = "user",
        IReadOnlyDictionary<string, JsonElement>? attributes = null,
        IReadOnlySet<string>? privateAttributes = null)
    {
        Key = key ?? string.Empty;
        Kind = string.IsNullOrWhiteSpace(kind) ? "user" : kind;
        Attributes = attributes ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        PrivateAttributes = privateAttributes ?? new HashSet<string>(StringComparer.Ordinal);
    }

    public string Key { get; }
    public string Kind { get; }
    public IReadOnlyDictionary<string, JsonElement> Attributes { get; }
    public IReadOnlySet<string> PrivateAttributes { get; }

    public bool TryGetAttribute(string attribute, out JsonElement value)
    {
        if (string.Equals(attribute, "key", StringComparison.Ordinal))
        {
            value = FeatureFlagJson.Element(Key);
            return true;
        }

        if (string.Equals(attribute, "kind", StringComparison.Ordinal))
        {
            value = FeatureFlagJson.Element(Kind);
            return true;
        }

        return Attributes.TryGetValue(attribute, out value);
    }

    public static EvaluationContext Create(string key, object? attributes = null, string kind = "user")
    {
        var values = attributes is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(attributes, FeatureFlagJson.Options), FeatureFlagJson.Options)
              ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return new EvaluationContext(key, kind, values);
    }
}

public sealed record EvaluationReason(EvaluationReasonKind Kind, int? RuleIndex = null, EvaluationErrorKind? ErrorKind = null)
{
    public static EvaluationReason Off() => new(EvaluationReasonKind.Off);
    public static EvaluationReason TargetMatch() => new(EvaluationReasonKind.TargetMatch);
    public static EvaluationReason SegmentMatch() => new(EvaluationReasonKind.SegmentMatch);
    public static EvaluationReason RuleMatch(int index) => new(EvaluationReasonKind.RuleMatch, index);
    public static EvaluationReason Fallthrough() => new(EvaluationReasonKind.Fallthrough);
    public static EvaluationReason PrerequisiteFailed() => new(EvaluationReasonKind.PrerequisiteFailed);
    public static EvaluationReason Error(EvaluationErrorKind error) => new(EvaluationReasonKind.Error, null, error);
}

public sealed record EvaluationResult(JsonElement Value, int? VariationIndex, EvaluationReason Reason)
{
    public static EvaluationResult Error(JsonElement fallback, EvaluationErrorKind kind) => new(fallback, null, EvaluationReason.Error(kind));
}
