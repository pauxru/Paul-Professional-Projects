using Lakehouse.Application.Metrics;

namespace Lakehouse.UnitTests;

/// <summary>
/// The metrics/semantic layer: a declarative <see cref="MetricQuery"/> resolves to SQL over the gold
/// layer using only allow-listed metrics/dimensions and a closed time-grain enum, which keeps it
/// injection-safe. Unknown metrics or dimensions are rejected rather than interpolated.
/// </summary>
public sealed class MetricsTests
{
    [Fact]
    public void Resolves_revenue_by_channel_monthly_to_sql()
    {
        var sql = MetricResolver.ToSql(new MetricQuery("revenue_usd", new[] { "channel" }, TimeGrain.Month));

        Assert.StartsWith("SELECT (order_date_key / 100) AS period, channel", sql, StringComparison.Ordinal);
        Assert.Contains("SUM(net_amount_usd) AS revenue_usd", sql, StringComparison.Ordinal);
        Assert.Contains("FROM fact_order_line", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY (order_date_key / 100), channel", sql, StringComparison.Ordinal);
        Assert.Contains("ORDER BY (order_date_key / 100), channel", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Grain_all_produces_no_grouping()
    {
        var sql = MetricResolver.ToSql(new MetricQuery("order_count", Array.Empty<string>(), TimeGrain.All));
        Assert.Equal("SELECT COUNT(DISTINCT order_id) AS order_count FROM fact_order_line", sql);
    }

    [Fact]
    public void Unknown_metric_is_rejected()
        => Assert.Throws<MetricException>(() => MetricResolver.ToSql(new MetricQuery("revenue_kes", Array.Empty<string>())));

    [Fact]
    public void Unknown_dimension_is_rejected()
        => Assert.Throws<MetricException>(() =>
            MetricResolver.ToSql(new MetricQuery("revenue_usd", new[] { "customer_email" })));

    [Fact]
    public void Injection_in_a_dimension_name_is_rejected_not_interpolated()
        => Assert.Throws<MetricException>(() =>
            MetricResolver.ToSql(new MetricQuery("revenue_usd", new[] { "channel; DROP TABLE fact_order_line" })));
}
