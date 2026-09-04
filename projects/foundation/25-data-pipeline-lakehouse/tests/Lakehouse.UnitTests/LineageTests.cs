using Lakehouse.Application.Lineage;
using Lakehouse.Application.Model;
using Lakehouse.Domain.Lineage;

namespace Lakehouse.UnitTests;

/// <summary>
/// Column-level lineage: multi-hop provenance (gold aggregate back to the raw source), impact analysis
/// (what breaks if a source column changes) and Mermaid rendering. The graph is built from the declared
/// transformations, so these assertions also protect the lineage↔transform correspondence.
/// </summary>
public sealed class LineageTests
{
    [Fact]
    public void Upstream_of_a_gold_column_reaches_silver_bronze_and_source()
    {
        var g = LineageCatalog.Build();

        var upstream = g.Upstream(new ColumnRef(Tables.AggDailyRevenue, "revenue_usd"));

        Assert.Contains(new ColumnRef(Tables.FactOrderLine, "net_amount_usd"), upstream);
        Assert.Contains(new ColumnRef(Tables.SilverOrderLines, "net_amount"), upstream);
        Assert.Contains(upstream, c => c.Dataset.StartsWith("bronze_", StringComparison.Ordinal)); // crossed silver→bronze
        Assert.Contains(upstream, c => c.Dataset.StartsWith("source_", StringComparison.Ordinal)); // reached the raw source
    }

    [Fact]
    public void Impact_of_a_source_column_reaches_the_gold_marts()
    {
        var g = LineageCatalog.Build();

        var impact = g.Impact(new ColumnRef(Tables.SilverOrderLines, "net_amount"));

        Assert.Contains(new ColumnRef(Tables.FactOrderLine, "net_amount_usd"), impact);
        Assert.Contains(new ColumnRef(Tables.AggDailyRevenue, "revenue_usd"), impact);
    }

    [Fact]
    public void Mermaid_renders_a_flowchart()
    {
        var mermaid = LineageCatalog.Build().ToMermaid();
        Assert.Contains("flowchart LR", mermaid, StringComparison.Ordinal);
        Assert.Contains("-->", mermaid, StringComparison.Ordinal);
    }
}
