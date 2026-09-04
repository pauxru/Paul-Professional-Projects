using Lakehouse.Application.Model;
using Lakehouse.Application.Pipelines;
using Lakehouse.Application.Serving;
using Lakehouse.Domain.Data;
using Lakehouse.Infrastructure.Serving;
using Lakehouse.UnitTests.Support;

namespace Lakehouse.UnitTests;

/// <summary>
/// The SQLite serving engine: gold tables are projected into SQLite and queried over a read-only
/// connection with a row cap. Only gold serving tables are loaded (bronze/silver PII never reach the
/// query surface), and every query passes through <see cref="SqlGuard"/>.
/// </summary>
public sealed class ServingEngineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProdFrom = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static void BuildGold(TempLake lh)
    {
        Seed(lh, Tables.DimCustomer, LakehouseModel.DimCustomer,
            Row.Of(("customer_sk", 1L), ("customer_id", "C1"), ("name", "N"), ("email", "e@x.io"), ("city", "Nairobi"),
                ("country", "KE"), ("currency", "USD"), ("segment", "retail"), ("loyalty_tier", (object?)null),
                ("valid_from", T0), ("valid_to", (object?)null), ("is_current", true), ("is_inferred", false)));
        Seed(lh, Tables.DimProduct, LakehouseModel.DimProduct,
            Row.Of(("product_sk", 100L), ("product_id", "P1"), ("name", "P"), ("category", "cat"), ("unit_price", 100m),
                ("currency", "USD"), ("active", true), ("valid_from", ProdFrom), ("valid_to", (object?)null),
                ("is_current", true), ("is_inferred", false)));
        Seed(lh, Tables.SilverOrders, LakehouseModel.SilverOrders,
            Row.Of(("order_id", "O1"), ("customer_id", "C1"), ("order_ts", T0.AddDays(5)),
                ("order_date", "2026-01-06"), ("channel", "web"), ("currency", "USD"), ("status", "ok")));
        Seed(lh, Tables.SilverOrderLines, LakehouseModel.SilverOrderLines,
            Row.Of(("order_line_id", "L1"), ("order_id", "O1"), ("product_id", "P1"), ("quantity", 1L),
                ("unit_price", 100m), ("discount", 0m), ("currency", "USD"), ("gross_amount", 100m), ("net_amount", 100m)),
            Row.Of(("order_line_id", "L2"), ("order_id", "O1"), ("product_id", "P1"), ("quantity", 2L),
                ("unit_price", 100m), ("discount", 0m), ("currency", "USD"), ("gross_amount", 200m), ("net_amount", 200m)));
        Seed(lh, Tables.SilverFx, LakehouseModel.SilverFx);

        var gold = new GoldBuilder(lh.Lake);
        gold.BuildFactOrderLine();
        gold.BuildAggDailyRevenue();
    }

    private static void Seed(TempLake lh, string name, Func<Lakehouse.Domain.Schemas.TableSchema> schema, params Row[] rows)
    {
        var t = lh.Lake.Table(name);
        t.Create(schema());
        if (rows.Length > 0) t.Append(rows);
    }

    private static SqliteQueryEngine Engine(TempLake lh)
    {
        var engine = new SqliteQueryEngine(lh.Lake, Path.Combine(lh.Root, "serving", "serving.db"));
        engine.Rebuild();
        return engine;
    }

    [Fact]
    public void Query_returns_gold_rows()
    {
        using var lh = new TempLake();
        BuildGold(lh);
        var result = Engine(lh).Query("SELECT date_key, revenue_usd FROM agg_daily_revenue");

        Assert.Equal(new[] { "date_key", "revenue_usd" }, result.Columns);
        var row = Assert.Single(result.Rows);
        Assert.Equal(20260106L, Convert.ToInt64(row[0]));
    }

    [Fact]
    public void Row_cap_truncates_results()
    {
        using var lh = new TempLake();
        BuildGold(lh);
        var result = Engine(lh).Query("SELECT * FROM fact_order_line", maxRows: 1);

        Assert.True(result.Truncated);
        Assert.Single(result.Rows);
    }

    [Fact]
    public void Only_gold_serving_tables_are_loaded()
    {
        using var lh = new TempLake();
        BuildGold(lh);
        var tables = Engine(lh).Tables();

        Assert.Contains("fact_order_line", tables);
        Assert.Contains("agg_daily_revenue", tables);
        Assert.DoesNotContain(tables, t => t.StartsWith("bronze_", StringComparison.Ordinal));
        Assert.DoesNotContain(tables, t => t.StartsWith("silver_", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_attempt_is_rejected()
    {
        using var lh = new TempLake();
        BuildGold(lh);
        var engine = Engine(lh);
        Assert.Throws<SqlGuardException>(() => engine.Query("DELETE FROM dim_customer"));
    }
}
