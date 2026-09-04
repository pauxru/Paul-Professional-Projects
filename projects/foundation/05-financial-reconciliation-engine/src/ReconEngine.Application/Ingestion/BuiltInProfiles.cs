using ReconEngine.Domain.Enums;
using ReconEngine.Domain.Normalization;

namespace ReconEngine.Application.Ingestion;

/// <summary>
/// The file-format profiles shipped with the engine. They match the layouts emitted by the synthetic
/// data generator and demonstrate CSV (internal + external) and fixed-width (external) ingestion.
/// </summary>
public static class BuiltInProfiles
{
    private static readonly ReferenceCanonicalizationRules Canon =
        new(Trim: true, ToUpper: true, RemoveWhitespace: false, StripPrefixes: new[] { "TXN_", "STL_" });

    /// <summary>Internal ledger export: TransactionId, MerchantOrderId, Amount, Currency, Date, Status.</summary>
    public static FileFormatProfile InternalCsv { get; } = new()
    {
        Name = "internal-csv",
        Format = RecordFileFormat.Csv,
        Source = RecordSource.Internal,
        HasHeader = true,
        Delimiter = ',',
        DecimalSeparator = ".",
        AmountInMinorUnits = false,
        DateFormats = new[] { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd" },
        SourceUtcOffset = "+03:00", // Nairobi
        DefaultCurrency = "KES",
        AmountSign = 1,
        Canonicalization = Canon,
        Fields = new[]
        {
            new FieldSpec(RecordField.Reference, 0, "TransactionId"),
            new FieldSpec(RecordField.CounterpartyReference, 1, "MerchantOrderId"),
            new FieldSpec(RecordField.Amount, 2, "Amount"),
            new FieldSpec(RecordField.Currency, 3, "Currency"),
            new FieldSpec(RecordField.Date, 4, "TransactionDate"),
            new FieldSpec(RecordField.Status, 5, "Status"),
        },
    };

    /// <summary>External settlement export: SettlementId, MerchantRef, NetAmount, Currency, Fee, Date, Status.</summary>
    public static FileFormatProfile ExternalCsv { get; } = new()
    {
        Name = "external-csv",
        Format = RecordFileFormat.Csv,
        Source = RecordSource.External,
        HasHeader = true,
        Delimiter = ',',
        DecimalSeparator = ".",
        AmountInMinorUnits = false,
        DateFormats = new[] { "yyyy-MM-dd", "yyyy-MM-dd HH:mm:ss" },
        SourceUtcOffset = "+00:00", // PSP settles in UTC
        DefaultCurrency = "KES",
        AmountSign = 1,
        Canonicalization = Canon,
        Fields = new[]
        {
            new FieldSpec(RecordField.Reference, 0, "SettlementId"),
            new FieldSpec(RecordField.CounterpartyReference, 1, "MerchantRef"),
            new FieldSpec(RecordField.Amount, 2, "NetAmount"),
            new FieldSpec(RecordField.Currency, 3, "Currency"),
            new FieldSpec(RecordField.Fee, 4, "Fee"),
            new FieldSpec(RecordField.Date, 5, "SettlementDate"),
            new FieldSpec(RecordField.Status, 6, "Status"),
        },
    };

    /// <summary>
    /// External settlement as fixed-width, minor units. Demonstrates the non-delimited path:
    /// [0..16) id, [16..32) ref, [32..46) net(minor), [46..49) ccy, [49..59) fee(minor), [59..67) yyyyMMdd, [67..77) status.
    /// </summary>
    public static FileFormatProfile ExternalFixedWidth { get; } = new()
    {
        Name = "external-fixed",
        Format = RecordFileFormat.FixedWidth,
        Source = RecordSource.External,
        HasHeader = false,
        AmountInMinorUnits = true,
        DateFormats = new[] { "yyyyMMdd" },
        SourceUtcOffset = "+00:00",
        DefaultCurrency = "KES",
        AmountSign = 1,
        Canonicalization = Canon,
        Fields = new[]
        {
            new FieldSpec(RecordField.Reference, Start: 0, Length: 16),
            new FieldSpec(RecordField.CounterpartyReference, Start: 16, Length: 16),
            new FieldSpec(RecordField.Amount, Start: 32, Length: 14),
            new FieldSpec(RecordField.Currency, Start: 46, Length: 3),
            new FieldSpec(RecordField.Fee, Start: 49, Length: 10),
            new FieldSpec(RecordField.Date, Start: 59, Length: 8),
            new FieldSpec(RecordField.Status, Start: 67, Length: 10),
        },
    };

    public static IReadOnlyList<FileFormatProfile> All { get; } =
        new[] { InternalCsv, ExternalCsv, ExternalFixedWidth };

    public static FileFormatProfile? ByName(string name) =>
        All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
