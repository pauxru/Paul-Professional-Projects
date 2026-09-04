using System.Text.RegularExpressions;

namespace Lakehouse.Application.Serving;

/// <summary>Raised when a submitted query violates the read-only serving contract.</summary>
public sealed class SqlGuardException(string message) : Exception(message);

/// <summary>
/// A defence-in-depth guard for the serving SQL API. The engine additionally opens a read-only
/// connection and caps rows/time, but this guard rejects unsafe queries up front: only a single
/// SELECT/WITH statement is allowed; DDL/DML, PRAGMA, ATTACH and statement batching (the classic
/// injection vector) are refused, as are SQL comments used to smuggle payloads.
/// </summary>
public static class SqlGuard
{
    private static readonly string[] ForbiddenKeywords =
    {
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "REPLACE", "TRUNCATE",
        "ATTACH", "DETACH", "PRAGMA", "VACUUM", "REINDEX", "TRIGGER", "GRANT", "REVOKE",
        "EXEC", "EXECUTE", "MERGE", "UPSERT"
    };

    /// <summary>Validate the query and return it normalised (single statement, no trailing semicolon).</summary>
    public static string Validate(string? sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new SqlGuardException("Empty query.");

        var trimmed = sql.Trim();

        if (trimmed.Contains("--", StringComparison.Ordinal) || trimmed.Contains("/*", StringComparison.Ordinal))
            throw new SqlGuardException("SQL comments are not allowed.");

        // Strip a single trailing semicolon, then reject any remaining statement separator.
        var normalised = trimmed.TrimEnd(';', ' ', '\t', '\r', '\n');
        if (normalised.Contains(';', StringComparison.Ordinal))
            throw new SqlGuardException("Multiple statements are not allowed.");

        var upper = normalised.ToUpperInvariant();
        if (!upper.StartsWith("SELECT", StringComparison.Ordinal) && !upper.StartsWith("WITH", StringComparison.Ordinal))
            throw new SqlGuardException("Only read-only SELECT/WITH queries are permitted.");

        foreach (var keyword in ForbiddenKeywords)
            if (KeywordRegex(keyword).IsMatch(upper))
                throw new SqlGuardException($"Statement type '{keyword}' is not permitted on the read-only serving API.");

        return normalised;
    }

    private static Regex KeywordRegex(string keyword) => new($@"\b{keyword}\b", RegexOptions.CultureInvariant);
}
