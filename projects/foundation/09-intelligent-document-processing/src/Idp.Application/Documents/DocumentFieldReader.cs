using System.Globalization;
using Idp.Domain.Documents;

namespace Idp.Application.Documents;

/// <summary>Typed, culture-invariant accessors over a document's extracted fields.</summary>
public static class DocumentFieldReader
{
    private static readonly string[] DateFormats =
    {
        "yyyy-MM-dd", "yyyy/MM/dd", "dd/MM/yyyy", "dd-MM-yyyy", "d MMM yyyy", "dd MMM yyyy",
        "MMM d, yyyy", "yyyyMMdd"
    };

    public static string? Text(this Document document, string key)
    {
        var f = document.Field(key);
        if (f is null) return null;
        return string.IsNullOrWhiteSpace(f.NormalizedValue) ? f.RawValue : f.NormalizedValue;
    }

    public static decimal? Amount(this Document document, string key)
    {
        var raw = document.Text(key);
        return ParseAmount(raw);
    }

    public static decimal? ParseAmount(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var cleaned = new string(raw.Where(c => char.IsDigit(c) || c is '.' or '-').ToArray());
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var v)
            ? v
            : null;
    }

    public static DateTime? Date(this Document document, string key)
    {
        var raw = document.Text(key);
        return ParseDate(raw);
    }

    public static DateTime? ParseDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (DateTime.TryParseExact(raw, DateFormats, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var exact))
            return exact.Date;
        return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v)
            ? v.Date
            : null;
    }
}
