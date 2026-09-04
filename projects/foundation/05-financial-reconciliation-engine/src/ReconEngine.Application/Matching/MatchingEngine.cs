using ReconEngine.Application.Matching.Rules;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.Matching;

/// <summary>
/// Runs the matching rules in priority order. After each rule, the records it claimed are removed from
/// the pool the next rule sees, so a high-confidence exact match is never overridden by a fuzzier rule.
/// The engine is pure and deterministic: the same inputs and ruleset always produce the same matches.
/// </summary>
public sealed class MatchingEngine
{
    private readonly IReadOnlyList<IMatchingRule> _rules;

    public MatchingEngine(IEnumerable<IMatchingRule> rules) =>
        _rules = rules.OrderBy(r => r.Priority).ToList();

    /// <summary>The full pipeline in canonical priority order.</summary>
    public static IReadOnlyList<IMatchingRule> DefaultRules() => new IMatchingRule[]
    {
        new ExactReferenceMatchRule(),
        new CompositeMatchRule(),
        new AmountAndDateWindowMatchRule(),
        new ManyToOneMatchRule(),
        new OneToManyMatchRule(),
        new FeeAdjustedMatchRule(),
        new RefundMatchRule(),
    };

    public static MatchingEngine CreateDefault() => new(DefaultRules());

    public MatchingOutcome Run(
        IReadOnlyList<ReconRecord> internals,
        IReadOnlyList<ReconRecord> externals,
        MatchingRuleSetDefinition def)
    {
        var matched = new HashSet<Guid>();
        var accepted = new List<MatchCandidate>();

        foreach (var rule in _rules)
        {
            if (!rule.IsEnabled(def))
                continue;

            var poolInternal = internals.Where(r => !matched.Contains(r.Id)).ToList();
            var poolExternal = externals.Where(r => !matched.Contains(r.Id)).ToList();
            var pool = new MatchPool(poolInternal, poolExternal);

            foreach (var candidate in rule.Apply(pool, def))
            {
                // Defensive: never let a candidate reuse a record already claimed this run.
                if (candidate.AllRecordIds.Any(matched.Contains))
                    continue;

                foreach (var id in candidate.AllRecordIds)
                    matched.Add(id);
                accepted.Add(candidate);
            }
        }

        return new MatchingOutcome
        {
            Matches = accepted,
            UnmatchedInternal = internals.Where(r => !matched.Contains(r.Id)).ToList(),
            UnmatchedExternal = externals.Where(r => !matched.Contains(r.Id)).ToList(),
        };
    }
}
