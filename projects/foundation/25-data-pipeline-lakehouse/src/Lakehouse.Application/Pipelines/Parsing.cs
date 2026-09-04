using System.Globalization;
using System.Text.Json;
using Lakehouse.Domain.Data;

namespace Lakehouse.Application.Pipelines;

/// <summary>
/// Typed-parse helpers for the bronze→silver boundary. Every parse is total: it returns whether the raw
/// string was valid so the caller can route bad rows to quarantine instead of silently coercing them.
/// </summary>
public static class Parsing
{
    public static bool TryLong(string? raw, out long value)
        => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static bool TryDecimal(string? raw, out decimal value)
        => decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out value);

    public static bool TryTimestampUtc(string? raw, out DateTimeOffset value)
    {
        if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            value = parsed.ToUniversalTime(); // timezone normalisation
            return true;
        }
        value = default;
        return false;
    }

    public static bool IsBlank(string? raw) => string.IsNullOrWhiteSpace(raw);

    /// <summary>Serialize a raw row to JSON for the quarantine payload (so nothing is lost on rejection).</summary>
    public static string Payload(Row row)
    {
        var obj = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var col in row.Columns) obj[col] = row[col]?.ToString();
        return JsonSerializer.Serialize(obj);
    }
}
