using System.Security.Cryptography;
using System.Text;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Application.Exceptions;

/// <summary>
/// A proposed exception before it is materialised into an entity. Carries the records involved so the
/// orchestrator can derive a stable <see cref="ExceptionKey"/> and link record ids.
/// </summary>
public sealed record ExceptionDraft(
    ExceptionType Type,
    ExceptionSeverity Severity,
    string SuggestedAction,
    string Currency,
    long AmountMinor,
    IReadOnlyList<ReconRecord> Records)
{
    public string ExceptionKey => ExceptionKeyFactory.For(Type, Currency, Records.Select(r => r.RowHash));
    public IReadOnlyList<Guid> RecordIds => Records.Select(r => r.Id).ToList();
}

/// <summary>Derives a stable, content-based identity for an exception so it survives re-runs.</summary>
public static class ExceptionKeyFactory
{
    public static string For(ExceptionType type, string currency, IEnumerable<string> rowHashes)
    {
        var canonical = $"{type}|{currency}|{string.Join(',', rowHashes.OrderBy(x => x, StringComparer.Ordinal))}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>Maps an exception type + magnitude to a severity and a suggested resolution action.</summary>
public static class ExceptionMetadata
{
    public static ExceptionSeverity SeverityFor(ExceptionType type, long amountMinor, long highThreshold) => type switch
    {
        ExceptionType.CurrencyMismatch => ExceptionSeverity.Critical,
        ExceptionType.DuplicateInternal or ExceptionType.DuplicateExternal => ExceptionSeverity.Medium,
        ExceptionType.StatusMismatch => ExceptionSeverity.Medium,
        ExceptionType.FeeVariance => Math.Abs(amountMinor) >= highThreshold ? ExceptionSeverity.High : ExceptionSeverity.Low,
        ExceptionType.DateOutOfWindow => ExceptionSeverity.Low,
        ExceptionType.AmountMismatch => Math.Abs(amountMinor) >= highThreshold ? ExceptionSeverity.High : ExceptionSeverity.Medium,
        ExceptionType.MissingInExternal or ExceptionType.MissingInInternal =>
            Math.Abs(amountMinor) >= highThreshold ? ExceptionSeverity.High : ExceptionSeverity.Medium,
        _ => ExceptionSeverity.Medium,
    };

    public static string SuggestedActionFor(ExceptionType type) => type switch
    {
        ExceptionType.MissingInExternal => "Chase settlement with provider; if never settled, RaiseWithProvider.",
        ExceptionType.MissingInInternal => "Investigate un-booked settlement; Reprocess once internal record is posted.",
        ExceptionType.AmountMismatch => "Verify fees/FX; ManualMatch if explained, else RaiseWithProvider.",
        ExceptionType.CurrencyMismatch => "Do not net across currencies; escalate — likely a booking error.",
        ExceptionType.DuplicateInternal => "Confirm double-booking in the ledger; WriteOff or Ignore the duplicate.",
        ExceptionType.DuplicateExternal => "Confirm duplicate settlement line with provider; RaiseWithProvider.",
        ExceptionType.StatusMismatch => "Reconcile lifecycle (e.g. refunded vs captured); ManualMatch or Reprocess.",
        ExceptionType.FeeVariance => "Compare against fee schedule; RaiseWithProvider if provider over-charged.",
        ExceptionType.DateOutOfWindow => "Widen the window or confirm late settlement; ManualMatch if legitimate.",
        _ => "Investigate and classify.",
    };

    public static ExceptionDraft Draft(ExceptionType type, string currency, long amountMinor, IReadOnlyList<ReconRecord> records, long highThreshold)
        => new(type, SeverityFor(type, amountMinor, highThreshold), SuggestedActionFor(type), currency, amountMinor, records);
}
