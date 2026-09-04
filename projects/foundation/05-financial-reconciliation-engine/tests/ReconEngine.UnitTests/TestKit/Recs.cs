using ReconEngine.Application.Matching;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.Normalization;

namespace ReconEngine.UnitTests.TestKit;

/// <summary>
/// Terse factory helpers for hand-built <see cref="ReconRecord"/> fixtures. Amounts are minor units,
/// references are canonicalised the same way the ingestion pipeline does, and the row hash is populated
/// so duplicate detection behaves exactly as in production.
/// </summary>
public static class Recs
{
    private static readonly ReferenceCanonicalizationRules Canon =
        new(Trim: true, ToUpper: true, RemoveWhitespace: false, StripPrefixes: new[] { "TXN_", "STL_" });

    public static ReconRecord Internal(
        string reference,
        long amountMinor,
        string currency = "KES",
        DateOnly? date = null,
        TransactionStatus status = TransactionStatus.Captured,
        string? merchant = null,
        long? fee = null,
        int line = 1) =>
        Make(RecordSource.Internal, reference, amountMinor, currency, date, status, merchant, fee, line);

    public static ReconRecord External(
        string reference,
        long amountMinor,
        string currency = "KES",
        DateOnly? date = null,
        TransactionStatus status = TransactionStatus.Settled,
        string? merchant = null,
        long? fee = null,
        int line = 1) =>
        Make(RecordSource.External, reference, amountMinor, currency, date, status, merchant, fee, line);

    private static ReconRecord Make(
        RecordSource source, string reference, long amountMinor, string currency,
        DateOnly? date, TransactionStatus status, string? merchant, long? fee, int line)
    {
        var day = date ?? new DateOnly(2024, 1, 15);
        var canonical = ReferenceCanonicalizer.Canonicalize(reference, Canon);
        var record = new ReconRecord
        {
            Source = source,
            RawReference = reference,
            CanonicalReference = canonical,
            CounterpartyReference = merchant is null ? null : ReferenceCanonicalizer.Canonicalize(merchant, Canon),
            AmountMinor = amountMinor,
            Currency = currency,
            FeeMinor = fee,
            TransactionDateUtc = new DateTime(day.Year, day.Month, day.Day, 12, 0, 0, DateTimeKind.Utc),
            ValueDate = day,
            SourceTimeZone = "+00:00",
            Status = status,
            LineNumber = line,
            IngestedAtUtc = new DateTime(2024, 6, 1, 0, 0, 0, DateTimeKind.Utc),
            ReconStatus = ReconStatus.Pending,
        };
        record.RowHash = RowHasher.Hash(source.ToString(), canonical, amountMinor, currency, day, status.ToString());
        return record;
    }

    public static MatchPool Pool(IEnumerable<ReconRecord> internals, IEnumerable<ReconRecord> externals) =>
        new(internals.ToList(), externals.ToList());

    public static DateOnly Day(int year, int month, int day) => new(year, month, day);
}
