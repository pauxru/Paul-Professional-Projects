namespace FraudPipeline.Domain.Rules;

/// <summary>
/// The result of one rule firing.
///   Contribution: raw score contribution before ruleset-level weighting.
///   Reason: human-readable explanation for the case investigator.
/// </summary>
public sealed record RuleFiringResult(
    string RuleId,
    RuleKind Kind,
    int Weight,
    int Contribution,
    string Reason,
    IReadOnlyDictionary<string, string>? Evidence = null);
