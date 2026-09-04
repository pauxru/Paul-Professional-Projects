using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.Matching.Rules;

/// <summary>
/// Bounded subset-sum search. Subset-sum is NP-hard in general, so we deliberately cap both the group
/// cardinality (<paramref name="maxGroupSize"/>) and the candidate pool size before searching. With a
/// pool capped at <c>m</c> and groups capped at <c>k</c> the worst case is O(C(m,k)); the ascending sort
/// plus the positive-amount prune makes the typical case far cheaper. This trade-off — completeness for
/// a hard bound on work — is documented in ADR-0003.
/// </summary>
internal static class SubsetSum
{
    public static List<ReconRecord>? Find(IReadOnlyList<ReconRecord> ascendingCandidates, long target, long tolerance, int maxGroupSize)
    {
        var chosen = new List<ReconRecord>();
        var n = ascendingCandidates.Count;

        bool Dfs(int start, long sum)
        {
            if (chosen.Count >= 2 && Math.Abs(sum - target) <= tolerance)
                return true;
            if (chosen.Count >= maxGroupSize)
                return false;

            for (var i = start; i < n; i++)
            {
                var next = ascendingCandidates[i];
                // Prune: candidates are ascending and positive, so once we overshoot we cannot recover.
                if (sum + next.AmountMinor - tolerance > target)
                    break;

                chosen.Add(next);
                if (Dfs(i + 1, sum + next.AmountMinor))
                    return true;
                chosen.RemoveAt(chosen.Count - 1);
            }
            return false;
        }

        return Dfs(0, 0) ? new List<ReconRecord>(chosen) : null;
    }

    /// <summary>Deterministically order and cap a candidate pool around a target's value date.</summary>
    public static List<ReconRecord> Pool(IEnumerable<ReconRecord> source, ReconRecord target, int windowDays, int maxCandidates)
        => source
            .Where(r => r.AmountMinor > 0
                        && string.Equals(r.Currency, target.Currency, StringComparison.Ordinal)
                        && Math.Abs(r.ValueDate.DayNumber - target.ValueDate.DayNumber) <= windowDays)
            .OrderBy(r => Math.Abs(r.ValueDate.DayNumber - target.ValueDate.DayNumber))
            .ThenBy(r => r.AmountMinor)
            .ThenBy(r => r.RowHash, StringComparer.Ordinal)
            .Take(maxCandidates)
            .OrderBy(r => r.AmountMinor)
            .ThenBy(r => r.RowHash, StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// Rule 4a: many internal transactions settled by a single external line (e.g. a batched payout).
/// </summary>
public sealed class ManyToOneMatchRule : IMatchingRule
{
    public string RuleId => "many-to-one-subset-sum";
    public int Priority => 4;
    public MatchKind Kind => MatchKind.ManyToOne;

    public bool IsEnabled(MatchingRuleSetDefinition def) => def.ManyToOneEnabled;

    public IEnumerable<MatchCandidate> Apply(MatchPool pool, MatchingRuleSetDefinition def)
    {
        var usedInternal = new HashSet<Guid>();

        foreach (var ext in pool.Externals.Where(e => e.AmountMinor > 0)
                     .OrderBy(e => e.ValueDate.DayNumber).ThenBy(e => e.RowHash, StringComparer.Ordinal))
        {
            var candidates = SubsetSum.Pool(
                pool.Internals.Where(i => !usedInternal.Contains(i.Id)),
                ext, def.SubsetSumDateWindowDays, def.SubsetSumMaxCandidates);

            var subset = SubsetSum.Find(candidates, ext.AmountMinor, def.AmountToleranceMinor, def.SubsetSumMaxGroupSize);
            if (subset is null)
                continue;

            foreach (var r in subset)
                usedInternal.Add(r.Id);

            yield return new MatchCandidate
            {
                RuleId = RuleId,
                Kind = Kind,
                Confidence = 0.8m,
                Currency = ext.Currency,
                Internals = subset,
                Externals = new[] { ext },
                Predicates = new[]
                {
                    $"Σ({subset.Count}) internal == external {ext.Amount}",
                    $"group size {subset.Count} ≤ cap {def.SubsetSumMaxGroupSize}",
                    $"all within {def.SubsetSumDateWindowDays} day(s)",
                },
            };
        }
    }
}

/// <summary>
/// Rule 4b: one internal transaction split across several external settlement lines.
/// </summary>
public sealed class OneToManyMatchRule : IMatchingRule
{
    public string RuleId => "one-to-many-subset-sum";
    public int Priority => 5;
    public MatchKind Kind => MatchKind.OneToMany;

    public bool IsEnabled(MatchingRuleSetDefinition def) => def.OneToManyEnabled;

    public IEnumerable<MatchCandidate> Apply(MatchPool pool, MatchingRuleSetDefinition def)
    {
        var usedExternal = new HashSet<Guid>();

        foreach (var intern in pool.Internals.Where(i => i.AmountMinor > 0)
                     .OrderBy(i => i.ValueDate.DayNumber).ThenBy(i => i.RowHash, StringComparer.Ordinal))
        {
            var candidates = SubsetSum.Pool(
                pool.Externals.Where(e => !usedExternal.Contains(e.Id)),
                intern, def.SubsetSumDateWindowDays, def.SubsetSumMaxCandidates);

            var subset = SubsetSum.Find(candidates, intern.AmountMinor, def.AmountToleranceMinor, def.SubsetSumMaxGroupSize);
            if (subset is null)
                continue;

            foreach (var r in subset)
                usedExternal.Add(r.Id);

            yield return new MatchCandidate
            {
                RuleId = RuleId,
                Kind = Kind,
                Confidence = 0.8m,
                Currency = intern.Currency,
                Internals = new[] { intern },
                Externals = subset,
                Predicates = new[]
                {
                    $"internal {intern.Amount} == Σ({subset.Count}) external",
                    $"group size {subset.Count} ≤ cap {def.SubsetSumMaxGroupSize}",
                    $"all within {def.SubsetSumDateWindowDays} day(s)",
                },
            };
        }
    }
}
