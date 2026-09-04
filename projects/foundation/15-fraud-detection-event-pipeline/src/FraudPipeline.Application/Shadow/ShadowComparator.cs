using FraudPipeline.Application.Abstractions;
using FraudPipeline.Domain.ValueObjects;

namespace FraudPipeline.Application.Shadow;

public sealed record ShadowComparison(
    int TotalPairs,
    int Matches,
    int Differences,
    IReadOnlyDictionary<string, int> DifferenceByDecision,
    double MatchRate);

/// <summary>
/// Computes decision-delta between live and shadow rulesets over recent transactions.
/// Uses persisted decisions with Shadow=true and Shadow=false for the same TransactionRef.
/// </summary>
public sealed class ShadowComparator
{
    private readonly IScoringDecisionRepository _decisions;

    public ShadowComparator(IScoringDecisionRepository decisions) => _decisions = decisions;

    public async Task<ShadowComparison> CompareAsync(int limit, CancellationToken ct = default)
    {
        var live = await _decisions.ListRecentAsync(limit, shadow: false, ct);
        var shadow = await _decisions.ListRecentAsync(limit, shadow: true, ct);
        var shadowByRef = new Dictionary<string, Domain.Entities.ScoringDecision>(StringComparer.Ordinal);
        foreach (var s in shadow) shadowByRef[s.TransactionRef] = s;

        int total = 0;
        int matches = 0;
        var diffs = new Dictionary<string, int>();
        foreach (var l in live)
        {
            if (!shadowByRef.TryGetValue(l.TransactionRef, out var s)) continue;
            total++;
            if (l.Decision == s.Decision) matches++;
            else
            {
                var key = $"{l.Decision}->{s.Decision}";
                diffs[key] = diffs.GetValueOrDefault(key) + 1;
            }
        }
        return new ShadowComparison(
            TotalPairs: total,
            Matches: matches,
            Differences: total - matches,
            DifferenceByDecision: diffs,
            MatchRate: total == 0 ? 0 : (double)matches / total);
    }
}
