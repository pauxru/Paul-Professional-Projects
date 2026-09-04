using System.Text;

namespace Lakehouse.Application.Metrics;

/// <summary>Raised when a metric query references an unknown metric or a non-allow-listed dimension.</summary>
public sealed class MetricException(string message) : Exception(message);

/// <summary>
/// Resolves a declarative <see cref="MetricQuery"/> into SQL against the gold layer. Only known metrics
/// and allow-listed dimensions can appear, and the time grain comes from a closed enum — so no
/// free-form user text is interpolated into SQL, which keeps the semantic layer injection-safe.
/// </summary>
public static class MetricResolver
{
    public static string ToSql(MetricQuery query)
    {
        var metric = MetricCatalog.Find(query.Metric)
            ?? throw new MetricException($"Unknown metric '{query.Metric}'.");

        foreach (var dim in query.Dimensions)
            if (!metric.Dimensions.Contains(dim, StringComparer.Ordinal))
                throw new MetricException($"Dimension '{dim}' is not available for metric '{metric.Name}'.");

        var selects = new List<string>();
        var groups = new List<string>();

        if (query.Grain != TimeGrain.All)
        {
            var grain = GrainExpression(query.Grain, metric.DateKeyColumn);
            selects.Add($"{grain} AS period");
            groups.Add(grain);
        }

        foreach (var dim in query.Dimensions)
        {
            selects.Add(dim);
            groups.Add(dim);
        }

        selects.Add($"{metric.Expression} AS {metric.Name}");

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(", ", selects));
        sb.Append(" FROM ").Append(metric.Table);
        if (groups.Count > 0)
        {
            sb.Append(" GROUP BY ").Append(string.Join(", ", groups));
            sb.Append(" ORDER BY ").Append(string.Join(", ", groups));
        }
        return sb.ToString();
    }

    /// <summary>Derive a time-grain bucket from an integer yyyymmdd date key using pure arithmetic.</summary>
    private static string GrainExpression(TimeGrain grain, string dateKey) => grain switch
    {
        TimeGrain.Day => dateKey,
        TimeGrain.Month => $"({dateKey} / 100)",                                   // yyyymm
        TimeGrain.Quarter => $"({dateKey} / 10000 * 10 + (({dateKey} / 100 % 100) - 1) / 3 + 1)", // yyyyq
        TimeGrain.Year => $"({dateKey} / 10000)",                                  // yyyy
        _ => "0"
    };
}
