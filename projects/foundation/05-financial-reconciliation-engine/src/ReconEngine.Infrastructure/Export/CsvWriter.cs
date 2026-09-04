using System.Text;

namespace ReconEngine.Infrastructure.Export;

/// <summary>
/// Writes CSV that is safe to open in a spreadsheet. Two threats are handled: standard RFC-4180 quoting
/// (so delimiters, quotes and newlines inside a value cannot break the row), and CSV/formula injection —
/// any value beginning with <c>= + - @</c> or a control character is prefixed with a single quote so a
/// spreadsheet treats it as text rather than executing it as a formula.
/// </summary>
public static class CsvWriter
{
    private const char Delimiter = ',';

    public static string Write(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var sb = new StringBuilder();
        sb.Append(string.Join(Delimiter, headers.Select(Encode)));
        sb.Append("\r\n");
        foreach (var row in rows)
        {
            sb.Append(string.Join(Delimiter, row.Select(Encode)));
            sb.Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Neutralise a leading formula trigger, then apply RFC-4180 quoting when needed.</summary>
    public static string Encode(string? value)
    {
        var v = value ?? string.Empty;

        if (v.Length > 0 && IsFormulaTrigger(v[0]))
            v = "'" + v;

        var mustQuote = v.Contains(Delimiter) || v.Contains('"') || v.Contains('\n') || v.Contains('\r');
        if (mustQuote)
            v = "\"" + v.Replace("\"", "\"\"") + "\"";

        return v;
    }

    private static bool IsFormulaTrigger(char c)
        => c is '=' or '+' or '-' or '@' or '\t' or '\r';
}
