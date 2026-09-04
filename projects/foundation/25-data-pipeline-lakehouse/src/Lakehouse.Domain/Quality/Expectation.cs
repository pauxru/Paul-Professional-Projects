using System.Globalization;
using Lakehouse.Domain.Data;

namespace Lakehouse.Domain.Quality;

/// <summary>
/// A declarative data-quality expectation. Implementations are pure functions of the rows (plus any
/// baseline captured at construction), which keeps them trivially testable and free of I/O.
/// </summary>
public abstract class Expectation
{
    protected Expectation(string name, Severity severity, string column)
    {
        Name = name;
        Severity = severity;
        Column = column;
    }

    public string Name { get; }
    public Severity Severity { get; }
    public string Column { get; }
    protected abstract string Kind { get; }

    public abstract ExpectationResult Evaluate(IReadOnlyList<Row> rows);

    protected ExpectationResult Pass(long evaluated, string message)
        => new(Name, Kind, Column, Severity, true, evaluated, 0, message);

    protected ExpectationResult Fail(long evaluated, long failed, string message)
        => new(Name, Kind, Column, Severity, false, evaluated, failed, message);

    protected static string Sample(IEnumerable<string> values) =>
        string.Join(", ", values.Take(3).Select(v => "'" + v + "'"));
}

/// <summary>Every value in a required column must be present.</summary>
public sealed class NotNullExpectation(string column, Severity severity = Severity.Fail)
    : Expectation($"not_null:{column}", severity, column)
{
    protected override string Kind => "not_null";

    public override ExpectationResult Evaluate(IReadOnlyList<Row> rows)
    {
        var failing = rows.Where(r => r.IsNull(Column)).ToList();
        return failing.Count == 0
            ? Pass(rows.Count, $"All {rows.Count} rows have a non-null '{Column}'.")
            : Fail(rows.Count, failing.Count, $"{failing.Count} of {rows.Count} rows have a null '{Column}'.");
    }
}

/// <summary>The combination of one or more columns must be unique across the batch.</summary>
public sealed class UniqueExpectation(IReadOnlyList<string> columns, Severity severity = Severity.Fail)
    : Expectation($"unique:{string.Join("+", columns)}", severity, string.Join("+", columns))
{
    private readonly IReadOnlyList<string> _columns = columns;
    protected override string Kind => "unique";

    public override ExpectationResult Evaluate(IReadOnlyList<Row> rows)
    {
        var groups = rows
            .GroupBy(r => string.Join("\u0001", _columns.Select(c => r[c]?.ToString() ?? "\u0000")), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .ToList();
        var dupRows = groups.Sum(g => g.Count() - 1);
        return dupRows == 0
            ? Pass(rows.Count, $"'{Column}' is unique across {rows.Count} rows.")
            : Fail(rows.Count, dupRows, $"{groups.Count} duplicate key(s) in '{Column}' ({Sample(groups.Select(g => g.Key))}).");
    }
}

/// <summary>Numeric values must fall within an inclusive range. Nulls are ignored (use NotNull for those).</summary>
public sealed class AcceptedRangeExpectation(string column, decimal? min, decimal? max, Severity severity = Severity.Fail)
    : Expectation($"range:{column}", severity, column)
{
    protected override string Kind => "accepted_range";

    public override ExpectationResult Evaluate(IReadOnlyList<Row> rows)
    {
        var failing = new List<string>();
        foreach (var r in rows)
        {
            var v = r.GetDecimal(Column);
            if (v is null) continue;
            if ((min is not null && v < min) || (max is not null && v > max))
                failing.Add(v.Value.ToString(CultureInfo.InvariantCulture));
        }
        return failing.Count == 0
            ? Pass(rows.Count, $"'{Column}' within [{min}, {max}].")
            : Fail(rows.Count, failing.Count, $"{failing.Count} value(s) of '{Column}' outside [{min}, {max}] ({Sample(failing)}).");
    }
}

/// <summary>Values must belong to an allow-list of accepted values.</summary>
public sealed class AcceptedValuesExpectation(string column, IReadOnlyCollection<string> accepted, Severity severity = Severity.Fail)
    : Expectation($"accepted_values:{column}", severity, column)
{
    private readonly HashSet<string> _accepted = new(accepted, StringComparer.Ordinal);
    protected override string Kind => "accepted_values";

    public override ExpectationResult Evaluate(IReadOnlyList<Row> rows)
    {
        var failing = rows
            .Where(r => !r.IsNull(Column))
            .Select(r => r[Column]!.ToString()!)
            .Where(v => !_accepted.Contains(v))
            .ToList();
        return failing.Count == 0
            ? Pass(rows.Count, $"'{Column}' only contains accepted values.")
            : Fail(rows.Count, failing.Count, $"{failing.Count} unexpected value(s) in '{Column}' ({Sample(failing)}).");
    }
}

/// <summary>Foreign-key conformance: non-null values must exist in the parent key set.</summary>
public sealed class ReferentialIntegrityExpectation(string column, IReadOnlyCollection<string> parentKeys, Severity severity = Severity.Fail)
    : Expectation($"referential_integrity:{column}", severity, column)
{
    private readonly HashSet<string> _parent = new(parentKeys, StringComparer.Ordinal);
    protected override string Kind => "referential_integrity";

    public override ExpectationResult Evaluate(IReadOnlyList<Row> rows)
    {
        var failing = rows
            .Where(r => !r.IsNull(Column))
            .Select(r => r[Column]!.ToString()!)
            .Where(v => !_parent.Contains(v))
            .ToList();
        return failing.Count == 0
            ? Pass(rows.Count, $"All '{Column}' values resolve to a parent key.")
            : Fail(rows.Count, failing.Count, $"{failing.Count} orphan '{Column}' value(s) ({Sample(failing.Distinct())}).");
    }
}

/// <summary>Freshness/SLA: the newest timestamp in the column must be within a max lag of "now".</summary>
public sealed class FreshnessExpectation(string column, TimeSpan maxLag, DateTimeOffset asOf, Severity severity = Severity.Fail)
    : Expectation($"freshness:{column}", severity, column)
{
    protected override string Kind => "freshness";

    public override ExpectationResult Evaluate(IReadOnlyList<Row> rows)
    {
        var newest = rows.Select(r => r.GetTimestamp(Column)).Where(t => t is not null).Select(t => t!.Value).DefaultIfEmpty().Max();
        if (newest == default)
            return Fail(rows.Count, rows.Count, $"No usable timestamps in '{Column}' to assess freshness.");
        var lag = asOf - newest;
        return lag <= maxLag
            ? Pass(rows.Count, $"'{Column}' is fresh (lag {lag:g} <= {maxLag:g}).")
            : Fail(rows.Count, 1, $"'{Column}' is stale: lag {lag:g} exceeds SLA {maxLag:g}.");
    }
}

/// <summary>Row-count anomaly vs a rolling baseline: current count must be within tolerance of the mean.</summary>
public sealed class RowCountAnomalyExpectation(double baselineMean, double tolerance, Severity severity = Severity.Warn)
    : Expectation("row_count_anomaly", severity, "*")
{
    protected override string Kind => "row_count_anomaly";

    public override ExpectationResult Evaluate(IReadOnlyList<Row> rows)
    {
        if (baselineMean <= 0)
            return Pass(rows.Count, "No baseline yet; row-count anomaly check skipped.");
        var lower = baselineMean * (1 - tolerance);
        var upper = baselineMean * (1 + tolerance);
        return rows.Count >= lower && rows.Count <= upper
            ? Pass(rows.Count, $"Row count {rows.Count} within [{lower:F0}, {upper:F0}] of baseline {baselineMean:F0}.")
            : Fail(rows.Count, 1, $"Row count {rows.Count} outside [{lower:F0}, {upper:F0}] of baseline {baselineMean:F0}.");
    }
}

/// <summary>
/// Categorical distribution drift: the total-variation distance between the observed value distribution
/// and a baseline distribution must stay under a threshold (0 = identical, 1 = disjoint).
/// </summary>
public sealed class DistributionDriftExpectation(
    string column,
    IReadOnlyDictionary<string, double> baseline,
    double threshold,
    Severity severity = Severity.Warn)
    : Expectation($"distribution_drift:{column}", severity, column)
{
    protected override string Kind => "distribution_drift";

    public override ExpectationResult Evaluate(IReadOnlyList<Row> rows)
    {
        var observed = rows
            .Where(r => !r.IsNull(Column))
            .GroupBy(r => r[Column]!.ToString()!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (double)g.Count(), StringComparer.Ordinal);
        var total = observed.Values.Sum();
        if (total == 0) return Pass(rows.Count, "No rows to assess distribution drift.");

        var keys = observed.Keys.Union(baseline.Keys, StringComparer.Ordinal);
        double tvd = 0;
        foreach (var k in keys)
        {
            var p = observed.TryGetValue(k, out var o) ? o / total : 0;
            var q = baseline.TryGetValue(k, out var b) ? b : 0;
            tvd += Math.Abs(p - q);
        }
        tvd /= 2.0;

        return tvd <= threshold
            ? Pass(rows.Count, $"Distribution drift {tvd:F3} <= {threshold:F3} for '{Column}'.")
            : Fail(rows.Count, 1, $"Distribution drift {tvd:F3} exceeds {threshold:F3} for '{Column}'.");
    }
}
