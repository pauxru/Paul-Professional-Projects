using System.Globalization;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.Matching.Rules;

/// <summary>
/// Rule 1 (highest priority): an exact match on canonical reference + amount + currency. This is the
/// unambiguous case and scores full confidence. Uses an O(n) hash index keyed by the three fields, so
/// a settlement line is located in constant time rather than by scanning the candidate set.
/// </summary>
public sealed class ExactReferenceMatchRule : IMatchingRule
{
    public string RuleId => "exact-reference";
    public int Priority => 1;
    public MatchKind Kind => MatchKind.OneToOne;

    public bool IsEnabled(MatchingRuleSetDefinition def) => def.ExactReferenceEnabled;

    public IEnumerable<MatchCandidate> Apply(MatchPool pool, MatchingRuleSetDefinition def)
    {
        var index = new Dictionary<(string cur, string reference, long amount), Queue<ReconRecord>>();
        foreach (var ext in pool.Externals)
        {
            var key = (ext.Currency, ext.CanonicalReference, ext.AmountMinor);
            if (!index.TryGetValue(key, out var q))
                index[key] = q = new Queue<ReconRecord>();
            q.Enqueue(ext);
        }

        foreach (var intern in pool.Internals)
        {
            var key = (intern.Currency, intern.CanonicalReference, intern.AmountMinor);
            if (string.IsNullOrEmpty(intern.CanonicalReference))
                continue;
            if (index.TryGetValue(key, out var q) && q.Count > 0)
            {
                var ext = q.Dequeue();
                yield return new MatchCandidate
                {
                    RuleId = RuleId,
                    Kind = Kind,
                    Confidence = 1.0m,
                    Currency = intern.Currency,
                    Internals = new[] { intern },
                    Externals = new[] { ext },
                    Predicates = new[]
                    {
                        $"reference == {intern.CanonicalReference}",
                        $"amount == {intern.Amount}",
                        $"currency == {intern.Currency}",
                    },
                };
            }
        }
    }
}

/// <summary>
/// Rule 2: composite match on a secondary (merchant) reference + amount within tolerance + value date
/// within N days. Catches records whose primary reference differs but which share a merchant order id.
/// </summary>
public sealed class CompositeMatchRule : IMatchingRule
{
    public string RuleId => "composite-ref-amount-date";
    public int Priority => 2;
    public MatchKind Kind => MatchKind.OneToOne;

    public bool IsEnabled(MatchingRuleSetDefinition def) => def.CompositeEnabled;

    public IEnumerable<MatchCandidate> Apply(MatchPool pool, MatchingRuleSetDefinition def)
    {
        var index = new Dictionary<(string cur, string mref), List<ReconRecord>>();
        foreach (var ext in pool.Externals)
        {
            if (string.IsNullOrEmpty(ext.CounterpartyReference))
                continue;
            var key = (ext.Currency, ext.CounterpartyReference!);
            if (!index.TryGetValue(key, out var list))
                index[key] = list = new List<ReconRecord>();
            list.Add(ext);
        }

        var used = new HashSet<Guid>();
        foreach (var intern in pool.Internals)
        {
            if (string.IsNullOrEmpty(intern.CounterpartyReference))
                continue;
            if (!index.TryGetValue((intern.Currency, intern.CounterpartyReference!), out var candidates))
                continue;

            ReconRecord? best = null;
            foreach (var ext in candidates)
            {
                if (used.Contains(ext.Id))
                    continue;
                if (!MatchMath.WithinAmountTolerance(intern.AmountMinor, ext.AmountMinor, def.CompositeAmountToleranceMinor, 0m))
                    continue;
                if (MatchMath.DateDiffDays(intern, ext) > def.CompositeDateWindowDays)
                    continue;
                if (best is null || MatchMath.DateDiffDays(intern, ext) < MatchMath.DateDiffDays(intern, best))
                    best = ext;
            }

            if (best is null)
                continue;

            used.Add(best.Id);
            yield return new MatchCandidate
            {
                RuleId = RuleId,
                Kind = Kind,
                Confidence = 0.9m,
                Currency = intern.Currency,
                Internals = new[] { intern },
                Externals = new[] { best },
                Predicates = new[]
                {
                    $"merchantRef == {intern.CounterpartyReference}",
                    $"amount within ±{def.CompositeAmountToleranceMinor} minor units",
                    $"valueDate within {def.CompositeDateWindowDays} day(s)",
                },
            };
        }
    }
}

/// <summary>
/// Rule 3: fuzzy match on amount (absolute and/or percentage tolerance) and value date within a window,
/// ignoring the reference entirely. This is the catch-all for records whose identifiers do not line up
/// but whose money and timing do. Confidence decays with distance.
/// </summary>
public sealed class AmountAndDateWindowMatchRule : IMatchingRule
{
    public string RuleId => "amount-date-window";
    public int Priority => 3;
    public MatchKind Kind => MatchKind.OneToOne;

    public bool IsEnabled(MatchingRuleSetDefinition def) => def.AmountDateWindowEnabled;

    public IEnumerable<MatchCandidate> Apply(MatchPool pool, MatchingRuleSetDefinition def)
    {
        // Bucket externals by (currency, day) so we only scan a window-sized neighbourhood, not the whole set.
        var byDay = new Dictionary<(string cur, int day), List<ReconRecord>>();
        foreach (var ext in pool.Externals)
        {
            var key = (ext.Currency, ext.ValueDate.DayNumber);
            if (!byDay.TryGetValue(key, out var list))
                byDay[key] = list = new List<ReconRecord>();
            list.Add(ext);
        }

        var used = new HashSet<Guid>();
        foreach (var intern in pool.Internals)
        {
            if (intern.AmountMinor <= 0)
                continue; // negatives are reserved for the dedicated refund rule
            ReconRecord? best = null;
            long bestDiff = long.MaxValue;

            for (var offset = -def.AmountDateWindowDays; offset <= def.AmountDateWindowDays; offset++)
            {
                if (!byDay.TryGetValue((intern.Currency, intern.ValueDate.DayNumber + offset), out var bucket))
                    continue;
                foreach (var ext in bucket)
                {
                    if (used.Contains(ext.Id))
                        continue;
                    if (ext.AmountMinor <= 0)
                        continue;
                    if (!MatchMath.WithinAmountTolerance(intern.AmountMinor, ext.AmountMinor, def.AmountToleranceMinor, def.AmountTolerancePercent))
                        continue;
                    var diff = Math.Abs(intern.AmountMinor - ext.AmountMinor);
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
            var confidence = MatchMath.FuzzyConfidence(bestDiff, Math.Abs(intern.AmountMinor), MatchMath.DateDiffDays(intern, best), def.AmountDateWindowDays);
            yield return new MatchCandidate
            {
                RuleId = RuleId,
                Kind = Kind,
                Confidence = confidence,
                Currency = intern.Currency,
                Internals = new[] { intern },
                Externals = new[] { best },
                Predicates = new[]
                {
                    $"amount within tolerance (Δ={bestDiff} minor units)",
                    $"valueDate within {def.AmountDateWindowDays} day(s)",
                    $"currency == {intern.Currency}",
                },
            };
        }
    }
}
