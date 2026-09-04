using System.Globalization;
using ReconEngine.Domain.Abstractions;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;
using ReconEngine.Domain.Normalization;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Application.Ingestion;

/// <summary>Outcome of mapping one tokenised row: either a normalised record or a rejection reason.</summary>
public sealed record MapResult(ReconRecord? Record, string? Error)
{
    public bool Ok => Record is not null;
    public static MapResult Success(ReconRecord r) => new(r, null);
    public static MapResult Fail(string reason) => new(null, reason);
}

/// <summary>
/// Pure mapping from a tokenised row to a normalised <see cref="ReconRecord"/> using a
/// <see cref="FileFormatProfile"/>. Performs decimal-separator handling, minor/major unit conversion,
/// sign normalisation, timezone-to-UTC conversion, reference canonicalisation and row hashing. All
/// failures are captured as reasons (with the offending line number handled by the caller), never thrown.
/// </summary>
public static class RecordMapper
{
    public static MapResult Map(
        TokenizedRow row,
        FileFormatProfile profile,
        IReadOnlyDictionary<string, int>? headerIndex,
        IClock clock)
    {
        var reference = GetField(RecordField.Reference, row, profile, headerIndex);
        if (string.IsNullOrWhiteSpace(reference))
            return MapResult.Fail("Missing reference");

        var currency = GetField(RecordField.Currency, row, profile, headerIndex);
        currency = string.IsNullOrWhiteSpace(currency) ? profile.DefaultCurrency : currency.Trim().ToUpperInvariant();
        if (!CurrencyInfo.IsKnown(currency))
            return MapResult.Fail($"Unknown currency '{currency}'");

        var amountRaw = GetField(RecordField.Amount, row, profile, headerIndex);
        if (!TryParseMinor(amountRaw, profile, currency, out var amountMinor))
            return MapResult.Fail($"Unparseable amount '{amountRaw}'");

        var dateRaw = GetField(RecordField.Date, row, profile, headerIndex);
        if (!TryParseUtc(dateRaw, profile, out var utc))
            return MapResult.Fail($"Unparseable date '{dateRaw}'");

        long? feeMinor = null;
        var feeRaw = GetField(RecordField.Fee, row, profile, headerIndex);
        if (!string.IsNullOrWhiteSpace(feeRaw))
        {
            if (!TryParseMinor(feeRaw, profile, currency, out var f))
                return MapResult.Fail($"Unparseable fee '{feeRaw}'");
            feeMinor = f;
        }

        var status = ParseStatus(GetField(RecordField.Status, row, profile, headerIndex));
        var canonical = ReferenceCanonicalizer.Canonicalize(reference, profile.Canonicalization);
        var counterparty = GetField(RecordField.CounterpartyReference, row, profile, headerIndex);
        var valueDate = DateOnly.FromDateTime(utc);

        var record = new ReconRecord
        {
            Source = profile.Source,
            RawReference = reference.Trim(),
            CanonicalReference = canonical,
            CounterpartyReference = string.IsNullOrWhiteSpace(counterparty) ? null
                : ReferenceCanonicalizer.Canonicalize(counterparty, profile.Canonicalization),
            AmountMinor = amountMinor,
            Currency = currency,
            FeeMinor = feeMinor,
            TransactionDateUtc = utc,
            ValueDate = valueDate,
            SourceTimeZone = profile.SourceUtcOffset,
            Status = status,
            LineNumber = row.LineNumber,
            IngestedAtUtc = clock.UtcNow,
            ReconStatus = ReconStatus.Pending,
        };
        record.RowHash = RowHasher.Hash(
            profile.Source.ToString(), canonical, amountMinor, currency, valueDate, status.ToString());

        return MapResult.Success(record);
    }

    private static string? GetField(
        RecordField field, TokenizedRow row, FileFormatProfile profile, IReadOnlyDictionary<string, int>? headerIndex)
    {
        var spec = profile.Fields.FirstOrDefault(f => f.Field == field);
        if (spec is null)
            return null;

        if (profile.Format == RecordFileFormat.FixedWidth)
        {
            if (spec.Start is null || spec.Length is null || spec.Start.Value >= row.RawLine.Length)
                return string.Empty;
            var start = spec.Start.Value;
            var len = Math.Min(spec.Length.Value, row.RawLine.Length - start);
            return row.RawLine.Substring(start, len).Trim();
        }

        var index = spec.ColumnIndex ?? ResolveHeader(spec.Header, headerIndex);
        if (index < 0 || index >= row.Fields.Count)
            return null;
        return row.Fields[index];
    }

    private static int ResolveHeader(string? header, IReadOnlyDictionary<string, int>? headerIndex)
        => header is not null && headerIndex is not null && headerIndex.TryGetValue(header, out var i) ? i : -1;

    private static bool TryParseMinor(string? raw, FileFormatProfile profile, string currency, out long minor)
    {
        minor = 0;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        var text = raw.Trim();
        var negative = false;

        // Support both leading-minus and accountancy parentheses e.g. (12.34).
        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            negative = true;
            text = text[1..^1].Trim();
        }

        // Normalise grouping and decimal marks to invariant '.'.
        text = profile.DecimalSeparator == ","
            ? text.Replace(".", string.Empty).Replace(',', '.')
            : text.Replace(",", string.Empty);

        if (profile.AmountInMinorUnits)
        {
            if (!long.TryParse(text, NumberStyles.Integer | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var m))
                return false;
            minor = m;
        }
        else
        {
            if (!decimal.TryParse(text, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var major))
                return false;
            minor = Money.FromMajor(major, currency).MinorUnits;
        }

        if (negative)
            minor = -minor;
        minor *= profile.AmountSign;
        return true;
    }

    private static bool TryParseUtc(string? raw, FileFormatProfile profile, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (!DateTime.TryParseExact(
                raw.Trim(),
                profile.DateFormats.ToArray(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var local))
            return false;

        var offset = ParseOffset(profile.SourceUtcOffset);
        utc = new DateTimeOffset(local, offset).UtcDateTime;
        return true;
    }

    private static TimeSpan ParseOffset(string s)
    {
        s = s.Trim();
        if (s.Length == 0 || s is "Z" or "z")
            return TimeSpan.Zero;

        var sign = 1;
        if (s.StartsWith('+')) s = s[1..];
        else if (s.StartsWith('-')) { sign = -1; s = s[1..]; }

        if (!TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out var ts)
            && !TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out ts))
            return TimeSpan.Zero;

        return sign < 0 ? -ts : ts;
    }

    private static TransactionStatus ParseStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return TransactionStatus.Unknown;

        return raw.Trim().ToUpperInvariant() switch
        {
            "CAPTURED" or "CAPTURE" or "PAID" => TransactionStatus.Captured,
            "SETTLED" or "SETTLEMENT" or "COMPLETE" or "COMPLETED" => TransactionStatus.Settled,
            "REFUND" or "REFUNDED" => TransactionStatus.Refunded,
            "REVERSED" or "REVERSAL" or "VOID" => TransactionStatus.Reversed,
            "FAILED" or "DECLINED" => TransactionStatus.Failed,
            "PENDING" or "AUTHORIZED" or "AUTHORISED" => TransactionStatus.Pending,
            "CHARGEBACK" or "CHARGED_BACK" or "CHARGEDBACK" => TransactionStatus.ChargedBack,
            _ => TransactionStatus.Unknown,
        };
    }
}
