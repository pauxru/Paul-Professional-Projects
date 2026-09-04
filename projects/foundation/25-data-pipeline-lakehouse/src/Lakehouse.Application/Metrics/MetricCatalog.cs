using Lakehouse.Application.Model;

namespace Lakehouse.Application.Metrics;

/// <summary>The catalogue of named metrics exposed by the semantic layer.</summary>
public static class MetricCatalog
{
    public static readonly IReadOnlyList<MetricDefinition> All = new[]
    {
        new MetricDefinition("revenue_usd", "Net revenue converted to USD", Tables.FactOrderLine,
            "SUM(net_amount_usd)", "order_date_key", new[] { "channel", "currency" }),
        new MetricDefinition("order_count", "Distinct orders", Tables.FactOrderLine,
            "COUNT(DISTINCT order_id)", "order_date_key", new[] { "channel", "currency" }),
        new MetricDefinition("units_sold", "Units sold", Tables.FactOrderLine,
            "SUM(quantity)", "order_date_key", new[] { "channel", "currency" }),
        new MetricDefinition("line_count", "Order lines", Tables.FactOrderLine,
            "COUNT(*)", "order_date_key", new[] { "channel", "currency" }),
        new MetricDefinition("avg_order_value_usd", "Average order value in USD", Tables.FactOrderLine,
            "SUM(net_amount_usd) * 1.0 / COUNT(DISTINCT order_id)", "order_date_key", new[] { "channel", "currency" }),
        new MetricDefinition("sessions", "Clickstream sessions", Tables.FactClickstreamSession,
            "COUNT(*)", "session_date_key", new[] { "channel" }),
        new MetricDefinition("conversion_rate", "Share of sessions that purchased", Tables.FactClickstreamSession,
            "AVG(converted)", "session_date_key", new[] { "channel" })
    };

    public static MetricDefinition? Find(string name) =>
        All.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
}
