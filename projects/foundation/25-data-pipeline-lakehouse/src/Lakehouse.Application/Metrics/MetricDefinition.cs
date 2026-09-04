namespace Lakehouse.Application.Metrics;

/// <summary>Supported time grains for a metric query.</summary>
public enum TimeGrain { All, Day, Month, Quarter, Year }

/// <summary>
/// A named metric: a SQL aggregate expression over a gold table, with an allow-list of dimensions it can
/// be sliced by and the integer date-key column used for time grains. Keeping metrics declarative gives
/// the serving layer a semantic layer (business asks for "revenue_usd by channel monthly", not SQL).
/// </summary>
public sealed record MetricDefinition(
    string Name,
    string Description,
    string Table,
    string Expression,
    string DateKeyColumn,
    IReadOnlyList<string> Dimensions);

/// <summary>A request for a metric, sliced by zero or more (allow-listed) dimensions at a time grain.</summary>
public sealed record MetricQuery(string Metric, IReadOnlyList<string> Dimensions, TimeGrain Grain = TimeGrain.Month);
