using System.Globalization;
using System.Text.Json;
using LoanOrigination.Domain.Models;

namespace LoanOrigination.Domain.Rules;

public enum ConditionKind
{
    All,
    Any,
    Not,
    Comparison
}

public enum ComparisonOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    In,
    Contains
}

public sealed record ConditionDefinition(
    ConditionKind Kind,
    string? Field = null,
    ComparisonOperator? Operator = null,
    JsonElement? Value = null,
    IReadOnlyList<ConditionDefinition>? Children = null)
{
    public static ConditionDefinition All(params ConditionDefinition[] children) =>
        new(ConditionKind.All, Children: children);

    public static ConditionDefinition Any(params ConditionDefinition[] children) =>
        new(ConditionKind.Any, Children: children);

    public static ConditionDefinition Not(ConditionDefinition child) =>
        new(ConditionKind.Not, Children: [child]);

    public static ConditionDefinition Compare(string field, ComparisonOperator comparison, object value) =>
        new(ConditionKind.Comparison, field, comparison, JsonSerializer.SerializeToElement(value));
}

public sealed record RuleOutcome(
    RuleOutcomeType Type,
    decimal? Amount = null,
    string? DocumentType = null,
    string? Detail = null);

public sealed record RuleDefinition(
    string Id,
    int Version,
    int Priority,
    ConditionDefinition Condition,
    RuleOutcome Outcome,
    string Reason);

public sealed record RuleSetDefinition(
    string Id,
    int Version,
    string Name,
    DateTimeOffset EffectiveFrom,
    IReadOnlyList<RuleDefinition> Rules,
    bool IsActive = true);

public sealed record RuleTraceEntry(
    string RuleId,
    int RuleVersion,
    IReadOnlyDictionary<string, string> InputsRead,
    bool Matched,
    RuleOutcome Outcome,
    string Reason);

public sealed record DecisionTrace(
    string RulesetId,
    int RulesetVersion,
    RuleDecision Decision,
    decimal? AdjustedMaximumPrincipal,
    decimal RateAdjustmentPercentagePoints,
    IReadOnlyList<string> RequiredDocuments,
    IReadOnlyList<RuleTraceEntry> Rules)
{
    public bool IsReproducibleWith(DecisionTrace other) =>
        RulesetId == other.RulesetId &&
        RulesetVersion == other.RulesetVersion &&
        Decision == other.Decision &&
        AdjustedMaximumPrincipal == other.AdjustedMaximumPrincipal &&
        RateAdjustmentPercentagePoints == other.RateAdjustmentPercentagePoints &&
        RequiredDocuments.SequenceEqual(other.RequiredDocuments) &&
        Rules.Count == other.Rules.Count &&
        Rules.Zip(other.Rules).All(pair =>
            pair.First.RuleId == pair.Second.RuleId &&
            pair.First.RuleVersion == pair.Second.RuleVersion &&
            pair.First.Matched == pair.Second.Matched &&
            pair.First.Outcome == pair.Second.Outcome &&
            pair.First.Reason == pair.Second.Reason &&
            pair.First.InputsRead.OrderBy(input => input.Key, StringComparer.Ordinal)
                .SequenceEqual(pair.Second.InputsRead.OrderBy(input => input.Key, StringComparer.Ordinal)));
}

public sealed class DeclarativeRulesEngine
{
    public DecisionTrace Evaluate(RuleSetDefinition ruleset, ApplicantFacts facts)
    {
        if (!ruleset.IsActive)
        {
            throw new DomainException($"Ruleset {ruleset.Id} version {ruleset.Version} is inactive.");
        }

        var trace = new List<RuleTraceEntry>();
        decimal? adjustedMaximum = null;
        var rateAdjustment = 0m;
        var requiredDocuments = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var anyFailure = false;
        var anyReferral = false;

        foreach (var rule in ruleset.Rules.OrderBy(rule => rule.Priority).ThenBy(rule => rule.Id, StringComparer.Ordinal))
        {
            var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
            var matched = EvaluateCondition(rule.Condition, facts, inputs);
            trace.Add(new RuleTraceEntry(
                rule.Id,
                rule.Version,
                inputs,
                matched,
                rule.Outcome,
                matched ? rule.Reason : $"Not triggered: {rule.Reason}"));

            if (!matched)
            {
                continue;
            }

            switch (rule.Outcome.Type)
            {
                case RuleOutcomeType.Fail:
                    anyFailure = true;
                    break;
                case RuleOutcomeType.Refer:
                    anyReferral = true;
                    break;
                case RuleOutcomeType.AdjustLimit:
                    if (!rule.Outcome.Amount.HasValue || rule.Outcome.Amount <= 0m)
                    {
                        throw new DomainException($"Rule {rule.Id} has an invalid limit adjustment.");
                    }

                    adjustedMaximum = adjustedMaximum.HasValue
                        ? decimal.Min(adjustedMaximum.Value, rule.Outcome.Amount.Value)
                        : rule.Outcome.Amount;
                    break;
                case RuleOutcomeType.AdjustRate:
                    if (!rule.Outcome.Amount.HasValue)
                    {
                        throw new DomainException($"Rule {rule.Id} has no rate adjustment.");
                    }

                    rateAdjustment += rule.Outcome.Amount.Value;
                    break;
                case RuleOutcomeType.RequireDocument:
                    if (string.IsNullOrWhiteSpace(rule.Outcome.DocumentType))
                    {
                        throw new DomainException($"Rule {rule.Id} has no required document type.");
                    }

                    requiredDocuments.Add(rule.Outcome.DocumentType);
                    anyReferral = true;
                    break;
                case RuleOutcomeType.Pass:
                    break;
                default:
                    throw new DomainException($"Rule {rule.Id} uses an unsupported outcome.");
            }
        }

        var decision = anyFailure
            ? RuleDecision.Fail
            : anyReferral
                ? RuleDecision.Refer
                : RuleDecision.Pass;
        return new DecisionTrace(
            ruleset.Id,
            ruleset.Version,
            decision,
            adjustedMaximum,
            decimal.Round(rateAdjustment, 4, MidpointRounding.AwayFromZero),
            requiredDocuments.ToArray(),
            trace);
    }

    private static bool EvaluateCondition(
        ConditionDefinition condition,
        ApplicantFacts facts,
        IDictionary<string, string> inputs)
    {
        return condition.Kind switch
        {
            ConditionKind.All => RequireChildren(condition).All(child => EvaluateCondition(child, facts, inputs)),
            ConditionKind.Any => RequireChildren(condition).Any(child => EvaluateCondition(child, facts, inputs)),
            ConditionKind.Not => !EvaluateCondition(RequireSingleChild(condition), facts, inputs),
            ConditionKind.Comparison => EvaluateComparison(condition, facts, inputs),
            _ => throw new DomainException("Unknown condition kind.")
        };
    }

    private static bool EvaluateComparison(
        ConditionDefinition condition,
        ApplicantFacts facts,
        IDictionary<string, string> inputs)
    {
        if (string.IsNullOrWhiteSpace(condition.Field) || !condition.Operator.HasValue || !condition.Value.HasValue)
        {
            throw new DomainException("A comparison condition must declare field, operator, and value.");
        }

        var actual = ReadFact(condition.Field, facts);
        inputs[condition.Field] = FormatValue(actual);
        return condition.Operator.Value switch
        {
            ComparisonOperator.Equals => Equal(actual, condition.Value.Value),
            ComparisonOperator.NotEquals => !Equal(actual, condition.Value.Value),
            ComparisonOperator.GreaterThan => Numeric(actual, condition.Value.Value, (left, right) => left > right),
            ComparisonOperator.GreaterThanOrEqual => Numeric(actual, condition.Value.Value, (left, right) => left >= right),
            ComparisonOperator.LessThan => Numeric(actual, condition.Value.Value, (left, right) => left < right),
            ComparisonOperator.LessThanOrEqual => Numeric(actual, condition.Value.Value, (left, right) => left <= right),
            ComparisonOperator.In => In(actual, condition.Value.Value),
            ComparisonOperator.Contains => Contains(actual, condition.Value.Value),
            _ => throw new DomainException("Unknown comparison operator.")
        };
    }

    private static object ReadFact(string field, ApplicantFacts facts) => field switch
    {
        nameof(ApplicantFacts.AgeYears) => facts.AgeYears,
        nameof(ApplicantFacts.MonthlyNetIncome) => facts.MonthlyNetIncome,
        nameof(ApplicantFacts.MonthlyExpenses) => facts.MonthlyExpenses,
        nameof(ApplicantFacts.ExistingMonthlyDebtService) => facts.ExistingMonthlyDebtService,
        nameof(ApplicantFacts.Dependants) => facts.Dependants,
        nameof(ApplicantFacts.AgeOfBusinessMonths) => facts.AgeOfBusinessMonths,
        nameof(ApplicantFacts.IncomeStabilityMonths) => facts.IncomeStabilityMonths,
        nameof(ApplicantFacts.PastArrearsCount) => facts.PastArrearsCount,
        nameof(ApplicantFacts.CollateralValue) => facts.CollateralValue,
        nameof(ApplicantFacts.RequestedPrincipal) => facts.RequestedPrincipal,
        nameof(ApplicantFacts.TermMonths) => facts.TermMonths,
        nameof(ApplicantFacts.BureauGrade) => facts.BureauGrade,
        nameof(ApplicantFacts.KycStatus) => facts.KycStatus,
        nameof(ApplicantFacts.IsSme) => facts.IsSme,
        nameof(ApplicantFacts.Currency) => facts.Currency,
        nameof(ApplicantFacts.DebtServiceRatio) => facts.DebtServiceRatio,
        nameof(ApplicantFacts.DebtToIncomeRatio) => facts.DebtToIncomeRatio,
        nameof(ApplicantFacts.DisposableIncome) => facts.DisposableIncome,
        _ => throw new DomainException($"Unknown applicant fact field '{field}'.")
    };

    private static IReadOnlyList<ConditionDefinition> RequireChildren(ConditionDefinition condition)
    {
        if (condition.Children is not { Count: > 0 })
        {
            throw new DomainException($"{condition.Kind} condition requires at least one child.");
        }

        return condition.Children;
    }

    private static ConditionDefinition RequireSingleChild(ConditionDefinition condition)
    {
        var children = RequireChildren(condition);
        if (children.Count != 1)
        {
            throw new DomainException("Not condition requires exactly one child.");
        }

        return children[0];
    }

    private static bool Numeric(object actual, JsonElement expected, Func<decimal, decimal, bool> comparison)
    {
        var actualNumber = actual switch
        {
            int integer => integer,
            decimal decimalValue => decimalValue,
            _ => throw new DomainException("Numeric comparison was used with a non-numeric fact.")
        };

        if (!TryGetDecimal(expected, out var expectedNumber))
        {
            throw new DomainException("Numeric comparison requires a numeric value.");
        }

        return comparison(actualNumber, expectedNumber);
    }

    private static bool Equal(object actual, JsonElement expected) => actual switch
    {
        int integer when TryGetDecimal(expected, out var number) => integer == number,
        decimal decimalValue when TryGetDecimal(expected, out var number) => decimalValue == number,
        bool boolean when expected.ValueKind is JsonValueKind.True or JsonValueKind.False => boolean == expected.GetBoolean(),
        string text when expected.ValueKind == JsonValueKind.String =>
            string.Equals(text, expected.GetString(), StringComparison.OrdinalIgnoreCase),
        _ => false
    };

    private static bool In(object actual, JsonElement expected)
    {
        if (expected.ValueKind != JsonValueKind.Array)
        {
            throw new DomainException("In comparison requires an array value.");
        }

        return expected.EnumerateArray().Any(candidate => Equal(actual, candidate));
    }

    private static bool Contains(object actual, JsonElement expected)
    {
        if (actual is not string text || expected.ValueKind != JsonValueKind.String)
        {
            throw new DomainException("Contains comparison requires string values.");
        }

        return text.Contains(expected.GetString() ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetDecimal(JsonElement element, out decimal value)
    {
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDecimal(out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String &&
            decimal.TryParse(element.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            return true;
        }

        value = 0m;
        return false;
    }

    private static string FormatValue(object value) => value switch
    {
        decimal decimalValue => decimalValue.ToString("0.####", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty
    };
}
