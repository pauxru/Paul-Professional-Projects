using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.Exceptions;

/// <summary>
/// Classifies the records left unmatched after the pipeline into concrete exception types. Near-misses
/// are detected by reference collisions among the leftovers: a shared reference with a different amount
/// is an <see cref="ExceptionType.AmountMismatch"/>; a shared reference in a different currency is a
/// <see cref="ExceptionType.CurrencyMismatch"/>; a shared merchant reference just outside the date window
/// is a <see cref="ExceptionType.DateOutOfWindow"/>. Everything else is genuinely missing on one side.
/// </summary>
public sealed class ExceptionClassifier
{
    public IReadOnlyList<ExceptionDraft> Classify(
        IReadOnlyList<ReconRecord> unmatchedInternal,
        IReadOnlyList<ReconRecord> unmatchedExternal,
        MatchingRuleSetDefinition def,
        long highSeverityAmountMinor)
    {
        var drafts = new List<ExceptionDraft>();
        var consumedExternal = new HashSet<Guid>();

        // Indexes over the leftover externals.
        var byRefCurrency = new Dictionary<(string cur, string reference), List<ReconRecord>>();
        var byRefAnyCurrency = new Dictionary<string, List<ReconRecord>>();
        var byMerchantCurrency = new Dictionary<(string cur, string mref), List<ReconRecord>>();

        foreach (var ext in unmatchedExternal)
        {
            Add(byRefCurrency, (ext.Currency, ext.CanonicalReference), ext);
            Add(byRefAnyCurrency, ext.CanonicalReference, ext);
            if (!string.IsNullOrEmpty(ext.CounterpartyReference))
                Add(byMerchantCurrency, (ext.Currency, ext.CounterpartyReference!), ext);
        }

        foreach (var intern in unmatchedInternal)
        {
            // (a) same reference + currency but different amount ⇒ amount mismatch.
            var sameRefCur = Take(byRefCurrency, (intern.Currency, intern.CanonicalReference), consumedExternal,
                ext => ext.AmountMinor != intern.AmountMinor);
            if (sameRefCur is not null)
            {
                var diff = intern.AmountMinor - sameRefCur.AmountMinor;
                drafts.Add(ExceptionMetadata.Draft(ExceptionType.AmountMismatch, intern.Currency, diff,
                    new[] { intern, sameRefCur }, highSeverityAmountMinor));
                consumedExternal.Add(sameRefCur.Id);
                continue;
            }

            // (b) same reference, different currency ⇒ currency mismatch (never net across currencies).
            var diffCurrency = Take(byRefAnyCurrency, intern.CanonicalReference, consumedExternal,
                ext => !string.Equals(ext.Currency, intern.Currency, StringComparison.Ordinal));
            if (diffCurrency is not null)
            {
                drafts.Add(ExceptionMetadata.Draft(ExceptionType.CurrencyMismatch, intern.Currency, intern.AmountMinor,
                    new[] { intern, diffCurrency }, highSeverityAmountMinor));
                consumedExternal.Add(diffCurrency.Id);
                continue;
            }

            // (c) same merchant reference + close amount but outside the date window ⇒ date out of window.
            if (!string.IsNullOrEmpty(intern.CounterpartyReference))
            {
                var outOfWindow = Take(byMerchantCurrency, (intern.Currency, intern.CounterpartyReference!), consumedExternal,
                    ext => Math.Abs(ext.AmountMinor - intern.AmountMinor) <= def.CompositeAmountToleranceMinor
                           && Math.Abs(ext.ValueDate.DayNumber - intern.ValueDate.DayNumber) > def.CompositeDateWindowDays);
                if (outOfWindow is not null)
                {
                    drafts.Add(ExceptionMetadata.Draft(ExceptionType.DateOutOfWindow, intern.Currency, intern.AmountMinor,
                        new[] { intern, outOfWindow }, highSeverityAmountMinor));
                    consumedExternal.Add(outOfWindow.Id);
                    continue;
                }
            }

            // (d) nothing on the other side ⇒ missing in external.
            drafts.Add(ExceptionMetadata.Draft(ExceptionType.MissingInExternal, intern.Currency, intern.AmountMinor,
                new[] { intern }, highSeverityAmountMinor));
        }

        // Remaining unconsumed externals are settlements with no internal record.
        foreach (var ext in unmatchedExternal)
        {
            if (consumedExternal.Contains(ext.Id))
                continue;
            drafts.Add(ExceptionMetadata.Draft(ExceptionType.MissingInInternal, ext.Currency, ext.AmountMinor,
                new[] { ext }, highSeverityAmountMinor));
        }

        return drafts;
    }

    private static void Add<TKey>(Dictionary<TKey, List<ReconRecord>> map, TKey key, ReconRecord r) where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
            map[key] = list = new List<ReconRecord>();
        list.Add(r);
    }

    private static ReconRecord? Take<TKey>(
        Dictionary<TKey, List<ReconRecord>> map, TKey key, HashSet<Guid> consumed, Func<ReconRecord, bool> predicate)
        where TKey : notnull
    {
        if (!map.TryGetValue(key, out var list))
            return null;
        return list.FirstOrDefault(r => !consumed.Contains(r.Id) && predicate(r));
    }
}
