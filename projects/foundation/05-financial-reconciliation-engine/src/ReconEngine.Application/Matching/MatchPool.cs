using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.Matching;

/// <summary>
/// The set of still-unmatched records a rule may draw from. Rules build whatever lookup indexes
/// they need over these lists; the engine hands each rule a fresh pool that excludes anything a
/// higher-priority rule already claimed.
/// </summary>
public sealed class MatchPool
{
    public MatchPool(IReadOnlyList<ReconRecord> internals, IReadOnlyList<ReconRecord> externals)
    {
        Internals = internals;
        Externals = externals;
    }

    public IReadOnlyList<ReconRecord> Internals { get; }
    public IReadOnlyList<ReconRecord> Externals { get; }
}

/// <summary>A rule in the matching pipeline. Rules are pure: same pool + definition ⇒ same output.</summary>
public interface IMatchingRule
{
    /// <summary>Stable identifier recorded on every match this rule produces.</summary>
    string RuleId { get; }

    /// <summary>Lower number runs first.</summary>
    int Priority { get; }

    MatchKind Kind { get; }

    bool IsEnabled(MatchingRuleSetDefinition def);

    IEnumerable<MatchCandidate> Apply(MatchPool pool, MatchingRuleSetDefinition def);
}

/// <summary>The outcome of a full pipeline evaluation.</summary>
public sealed class MatchingOutcome
{
    public required IReadOnlyList<MatchCandidate> Matches { get; init; }
    public required IReadOnlyList<ReconRecord> UnmatchedInternal { get; init; }
    public required IReadOnlyList<ReconRecord> UnmatchedExternal { get; init; }
}
