using ReconEngine.Application.Exceptions;
using ReconEngine.Application.Matching;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.Reconciliation;

/// <summary>The complete, pure result of reconciling two sets of records. No persistence concepts leak in.</summary>
public sealed class ReconciliationResult
{
    public required IReadOnlyList<MatchCandidate> Matches { get; init; }
    public required IReadOnlyList<ExceptionDraft> Exceptions { get; init; }
    public required IReadOnlyList<CurrencyTotals> Totals { get; init; }
    public required bool BalancePassed { get; init; }
    public string? BalanceDetail { get; init; }
    public required IReadOnlyCollection<Guid> MatchedInternalIds { get; init; }
    public required IReadOnlyCollection<Guid> MatchedExternalIds { get; init; }

    public IReadOnlyDictionary<string, int> ExceptionBreakdown =>
        Exceptions.GroupBy(e => e.Type.ToString()).ToDictionary(g => g.Key, g => g.Count());

    public int MatchCount => Matches.Count;
    public int ExceptionCount => Exceptions.Count;
}

/// <summary>
/// The heart of the engine as a pure function: given internal records, external records, a ruleset
/// definition and the severity threshold, it deduplicates, runs the matching pipeline, flags fee and
/// status discrepancies on the matches, classifies the leftovers, computes per-currency totals and
/// self-checks the balance — all without touching a database. This is what the vast majority of the
/// unit tests exercise, so behaviour is pinned independently of EF Core.
/// </summary>
public sealed class ReconciliationCalculator
{
    private readonly MatchingEngine _engine;
    private readonly ExceptionClassifier _classifier;

    public ReconciliationCalculator(MatchingEngine engine, ExceptionClassifier classifier)
    {
        _engine = engine;
        _classifier = classifier;
    }

    public static ReconciliationCalculator CreateDefault() =>
        new(MatchingEngine.CreateDefault(), new ExceptionClassifier());

    public ReconciliationResult Calculate(
        IReadOnlyList<ReconRecord> internalRecords,
        IReadOnlyList<ReconRecord> externalRecords,
        MatchingRuleSetDefinition def,
        long highSeverityAmountMinor)
    {
        var (uniqueInternal, duplicateInternal) = DuplicateDetector.Detect(internalRecords);
        var (uniqueExternal, duplicateExternal) = DuplicateDetector.Detect(externalRecords);

        var outcome = _engine.Run(uniqueInternal, uniqueExternal, def);

        var matchedInternalIds = new HashSet<Guid>();
        var matchedExternalIds = new HashSet<Guid>();
        var drafts = new List<ExceptionDraft>();

        foreach (var candidate in outcome.Matches)
        {
            foreach (var r in candidate.Internals)
                matchedInternalIds.Add(r.Id);
            foreach (var r in candidate.Externals)
                matchedExternalIds.Add(r.Id);

            // Fee variance: still a match, but flag the discrepancy against the schedule.
            if (candidate.FeeVarianceMinor is { } variance)
            {
                drafts.Add(ExceptionMetadata.Draft(ExceptionType.FeeVariance, candidate.Currency, variance,
                    candidate.AllRecords.ToList(), highSeverityAmountMinor));
            }

            // Status mismatch on a one-to-one, non-refund match: reconcile the money, flag the lifecycle.
            if (candidate.Kind != MatchKind.Refund && candidate.Internals.Count == 1 && candidate.Externals.Count == 1)
            {
                var i = candidate.Internals[0];
                var e = candidate.Externals[0];
                if (StatusRules.Contradictory(i.Status, e.Status))
                {
                    drafts.Add(ExceptionMetadata.Draft(ExceptionType.StatusMismatch, candidate.Currency, i.AmountMinor,
                        new[] { i, e }, highSeverityAmountMinor));
                }
            }
        }

        foreach (var d in duplicateInternal)
            drafts.Add(ExceptionMetadata.Draft(ExceptionType.DuplicateInternal, d.Currency, d.AmountMinor, new[] { d }, highSeverityAmountMinor));
        foreach (var d in duplicateExternal)
            drafts.Add(ExceptionMetadata.Draft(ExceptionType.DuplicateExternal, d.Currency, d.AmountMinor, new[] { d }, highSeverityAmountMinor));

        drafts.AddRange(_classifier.Classify(outcome.UnmatchedInternal, outcome.UnmatchedExternal, def, highSeverityAmountMinor));

        var totals = ComputeTotals(internalRecords, externalRecords, matchedInternalIds, matchedExternalIds);
        var (passed, detail) = BalanceAssertion.Check(totals);

        return new ReconciliationResult
        {
            Matches = outcome.Matches,
            Exceptions = drafts,
            Totals = totals,
            BalancePassed = passed,
            BalanceDetail = detail,
            MatchedInternalIds = matchedInternalIds,
            MatchedExternalIds = matchedExternalIds,
        };
    }

    private static IReadOnlyList<CurrencyTotals> ComputeTotals(
        IReadOnlyList<ReconRecord> internalRecords,
        IReadOnlyList<ReconRecord> externalRecords,
        HashSet<Guid> matchedInternalIds,
        HashSet<Guid> matchedExternalIds)
    {
        var currencies = internalRecords.Select(r => r.Currency)
            .Concat(externalRecords.Select(r => r.Currency))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(c => c, StringComparer.Ordinal);

        var result = new List<CurrencyTotals>();
        foreach (var cur in currencies)
        {
            var ints = internalRecords.Where(r => r.Currency == cur).ToList();
            var exts = externalRecords.Where(r => r.Currency == cur).ToList();

            long matchedInt = ints.Where(r => matchedInternalIds.Contains(r.Id)).Sum(r => r.AmountMinor);
            long unmatchedInt = ints.Where(r => !matchedInternalIds.Contains(r.Id)).Sum(r => r.AmountMinor);
            long matchedExt = exts.Where(r => matchedExternalIds.Contains(r.Id)).Sum(r => r.AmountMinor);
            long unmatchedExt = exts.Where(r => !matchedExternalIds.Contains(r.Id)).Sum(r => r.AmountMinor);

            result.Add(new CurrencyTotals(
                cur,
                ints.Sum(r => r.AmountMinor), matchedInt, unmatchedInt,
                exts.Sum(r => r.AmountMinor), matchedExt, unmatchedExt));
        }

        return result;
    }
}
