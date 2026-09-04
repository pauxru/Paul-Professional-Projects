using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.Matching.Rules;

/// <summary>
/// Rule 5: fee-adjusted match. The internal gross equals the external net plus a fee. When the external
/// line carries an actual fee we reconcile <c>gross == net + actualFee</c>; otherwise the expected fee is
/// derived from the ruleset's fee schedule. Either way we compare the actual fee against the schedule and
/// surface the difference as <see cref="MatchCandidate.FeeVarianceMinor"/> so the run can raise a
/// FeeVariance exception while still counting the money as matched.
/// </summary>
public sealed class FeeAdjustedMatchRule : IMatchingRule
{
    public string RuleId => "fee-adjusted";
    public int Priority => 6;
    public MatchKind Kind => MatchKind.FeeAdjusted;

    public bool IsEnabled(MatchingRuleSetDefinition def) => def.FeeAdjustedEnabled;

    public IEnumerable<MatchCandidate> Apply(MatchPool pool, MatchingRuleSetDefinition def)
    {
        var index = new Dictionary<(string cur, string reference), Queue<ReconRecord>>();
        foreach (var ext in pool.Externals)
        {
            if (string.IsNullOrEmpty(ext.CanonicalReference))
                continue;
            var key = (ext.Currency, ext.CanonicalReference);
            if (!index.TryGetValue(key, out var q))
                index[key] = q = new Queue<ReconRecord>();
            q.Enqueue(ext);
        }

        foreach (var intern in pool.Internals)
        {
            if (string.IsNullOrEmpty(intern.CanonicalReference))
                continue;
            if (!index.TryGetValue((intern.Currency, intern.CanonicalReference), out var q) || q.Count == 0)
                continue;

            var ext = q.Peek();
            var actualFee = ext.FeeMinor ?? (intern.AmountMinor - ext.AmountMinor);
            if (actualFee <= 0)
                continue; // not a fee-adjusted relationship

            // gross must equal net + actual fee (within a minor-unit rounding tolerance).
            if (Math.Abs(intern.AmountMinor - (ext.AmountMinor + actualFee)) > def.FeeSchedule.ToleranceMinor)
                continue;
            if (MatchMath.DateDiffDays(intern, ext) > def.FeeAdjustedDateWindowDays)
                continue;

            q.Dequeue();

            var expectedFee = def.FeeSchedule.ExpectedFeeMinor(intern.AmountMinor, intern.Currency);
            var variance = actualFee - expectedFee;
            long? flaggedVariance = Math.Abs(variance) > def.FeeSchedule.ToleranceMinor ? variance : null;

            yield return new MatchCandidate
            {
                RuleId = RuleId,
                Kind = Kind,
                Confidence = 0.85m,
                Currency = intern.Currency,
                Internals = new[] { intern },
                Externals = new[] { ext },
                ExpectedFeeMinor = expectedFee,
                FeeVarianceMinor = flaggedVariance,
                Predicates = new[]
                {
                    $"reference == {intern.CanonicalReference}",
                    $"gross {intern.Amount} == net {ext.Amount} + fee {new Money(actualFee, intern.Currency)}",
                    $"expectedFee == {new Money(expectedFee, intern.Currency)}",
                    flaggedVariance is null
                        ? "fee within schedule tolerance"
                        : $"FEE VARIANCE == {new Money(variance, intern.Currency)}",
                },
            };
        }
    }
}

/// <summary>
/// Rule 6: refund match. A negative internal amount pairs to a negative external amount of equal
/// magnitude within a wider date window; the external reference points back to the original capture.
/// Restricting to negative amounts keeps refunds from being swallowed by the fuzzy amount rule.
/// </summary>
public sealed class RefundMatchRule : IMatchingRule
{
    public string RuleId => "refund";
    public int Priority => 7;
    public MatchKind Kind => MatchKind.Refund;

    public bool IsEnabled(MatchingRuleSetDefinition def) => def.RefundEnabled;

    public IEnumerable<MatchCandidate> Apply(MatchPool pool, MatchingRuleSetDefinition def)
    {
        var byDay = new Dictionary<(string cur, int day), List<ReconRecord>>();
        foreach (var ext in pool.Externals.Where(e => e.AmountMinor < 0))
        {
            var key = (ext.Currency, ext.ValueDate.DayNumber);
            if (!byDay.TryGetValue(key, out var list))
                byDay[key] = list = new List<ReconRecord>();
            list.Add(ext);
        }

        var used = new HashSet<Guid>();
        foreach (var intern in pool.Internals.Where(i => i.AmountMinor < 0)
                     .OrderBy(i => i.ValueDate.DayNumber).ThenBy(i => i.RowHash, StringComparer.Ordinal))
        {
            ReconRecord? best = null;
            long bestDiff = long.MaxValue;

            for (var offset = -def.RefundDateWindowDays; offset <= def.RefundDateWindowDays; offset++)
            {
                if (!byDay.TryGetValue((intern.Currency, intern.ValueDate.DayNumber + offset), out var bucket))
                    continue;
                foreach (var ext in bucket)
                {
                    if (used.Contains(ext.Id))
                        continue;
                    var diff = Math.Abs(intern.AmountMinor - ext.AmountMinor);
                    if (diff > def.AmountToleranceMinor)
                        continue;
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = ext;
                    }
                }
            }

            if (best is null)
                continue;

            used.Add(best.Id);
            yield return new MatchCandidate
            {
                RuleId = RuleId,
                Kind = Kind,
                Confidence = 0.85m,
                Currency = intern.Currency,
                Internals = new[] { intern },
                Externals = new[] { best },
                Predicates = new[]
                {
                    $"negative amount {intern.Amount} pairs refund",
                    $"|Δ| == {bestDiff} minor units",
                    $"original reference {best.CanonicalReference}",
                    $"within {def.RefundDateWindowDays} day(s)",
                },
            };
        }
    }
}
