using System.Globalization;
using System.Text;
using ReconEngine.Domain.Entities;
using ReconEngine.Domain.Enums;

namespace ReconEngine.Application.DataGeneration;

/// <summary>Locations of the files written for a generated dataset.</summary>
public sealed record GeneratedFiles(
    string Directory,
    string InternalCsv,
    string ExternalCsv,
    string ExternalFixedWidth,
    string Manifest);

/// <summary>
/// Serialises a <see cref="GeneratedDataset"/> to disk in the exact layouts the built-in file-format
/// profiles expect: internal CSV (major units, Nairobi local time), external CSV (major units, UTC) and
/// an external fixed-width variant (minor units). Round-tripping these files back through the import
/// pipeline reproduces byte-identical normalised records, which the integration tests rely on.
/// </summary>
public static class DatasetFileWriter
{
    public static async Task<GeneratedFiles> WriteAsync(
        GeneratedDataset dataset, string directory, bool writeFixedWidth = true, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(directory);

        var internalPath = Path.Combine(directory, "internal.csv");
        var externalPath = Path.Combine(directory, "external.csv");
        var fixedPath = Path.Combine(directory, "external.fixed.txt");
        var manifestPath = Path.Combine(directory, "manifest.json");

        await WriteInternalCsvAsync(dataset.Internal, internalPath, ct);
        await WriteExternalCsvAsync(dataset.External, externalPath, ct);
        if (writeFixedWidth)
            await WriteExternalFixedWidthAsync(dataset.External, fixedPath, ct);
        await File.WriteAllTextAsync(manifestPath, dataset.Manifest.Serialize(), ct);

        return new GeneratedFiles(directory, internalPath, externalPath, fixedPath, manifestPath);
    }

    private static async Task WriteInternalCsvAsync(IReadOnlyList<ReconRecord> records, string path, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync("TransactionId,MerchantOrderId,Amount,Currency,TransactionDate,Status");
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            // Internal source timezone is +03:00; stamping local noon keeps the UTC value date stable.
            var localDate = r.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " 12:00:00";
            await writer.WriteLineAsync(string.Join(',',
                Csv(r.RawReference),
                Csv(r.CounterpartyReference ?? string.Empty),
                Major(r.AmountMinor, r.Currency),
                r.Currency,
                localDate,
                r.Status.ToString()));
        }
    }

    private static async Task WriteExternalCsvAsync(IReadOnlyList<ReconRecord> records, string path, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        await writer.WriteLineAsync("SettlementId,MerchantRef,NetAmount,Currency,Fee,SettlementDate,Status");
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteLineAsync(string.Join(',',
                Csv(r.RawReference),
                Csv(r.CounterpartyReference ?? string.Empty),
                Major(r.AmountMinor, r.Currency),
                r.Currency,
                r.FeeMinor is { } fee ? Major(fee, r.Currency) : string.Empty,
                r.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.Status.ToString()));
        }
    }

    private static async Task WriteExternalFixedWidthAsync(IReadOnlyList<ReconRecord> records, string path, CancellationToken ct)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        foreach (var r in records)
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder(77);
            sb.Append(Fixed(r.RawReference, 16));                                     // [0..16)  settlement id
            sb.Append(Fixed(r.CounterpartyReference ?? string.Empty, 16));            // [16..32) merchant ref
            sb.Append(FixedRight(r.AmountMinor.ToString(CultureInfo.InvariantCulture), 14)); // [32..46) net minor
            sb.Append(Fixed(r.Currency, 3));                                          // [46..49) currency
            sb.Append(FixedRight(r.FeeMinor?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, 10)); // [49..59) fee minor
            sb.Append(r.ValueDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture));// [59..67) date
            sb.Append(Fixed(r.Status.ToString(), 10));                               // [67..77) status
            await writer.WriteLineAsync(sb.ToString());
        }
    }

    private static string Major(long minor, string currency)
    {
        var scale = ReconEngine.Domain.ValueObjects.CurrencyInfo.ScaleFor(currency);
        var major = decimal.Divide(minor, scale);
        var decimals = ReconEngine.Domain.ValueObjects.CurrencyInfo.DecimalsFor(currency);
        return major.ToString("F" + decimals, CultureInfo.InvariantCulture);
    }

    private static string Csv(string value)
    {
        if (value.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0)
            return value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    private static string Fixed(string value, int width) =>
        value.Length >= width ? value[..width] : value.PadRight(width);

    private static string FixedRight(string value, int width) =>
        value.Length >= width ? value[..width] : value.PadLeft(width);
}
