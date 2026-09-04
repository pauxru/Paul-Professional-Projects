using Lakehouse.Application.Model;
using Lakehouse.Application.Pipelines;
using Lakehouse.Domain.Data;
using Lakehouse.UnitTests.Support;

namespace Lakehouse.UnitTests;

/// <summary>
/// Gold star-schema build: the effective-version SCD2 join in the fact (the classic bug), late-arriving
/// dimension inference, and currency normalisation to USD via the FX dimension. Tables are hand-seeded so
/// each behaviour is isolated.
/// </summary>
public sealed class GoldTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T1 = new(2026, 2, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ProdFrom = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static void Seed(TempLake lh, string name, Func<Lakehouse.Domain.Schemas.TableSchema> schema, params Row[] rows)
    {
        var t = lh.Lake.Table(name);
        t.Create(schema());
        if (rows.Length > 0) t.Append(rows);
    }

    private static Row Cust(long sk, string id, DateTimeOffset from, DateTimeOffset? to, string segment, bool current) =>
        Row.Of(("customer_sk", sk), ("customer_id", id), ("name", "N"), ("email", "e@x.io"), ("city", "Nairobi"),
            ("country", "KE"), ("currency", "USD"), ("segment", segment), ("loyalty_tier", (object?)null),
            ("valid_from", from), ("valid_to", (object?)to), ("is_current", current), ("is_inferred", false));

    private static Row Prod(long sk, string id) =>
        Row.Of(("product_sk", sk), ("product_id", id), ("name", "P"), ("category", "cat"), ("unit_price", 100m),
            ("currency", "USD"), ("active", true), ("valid_from", ProdFrom), ("valid_to", (object?)null),
            ("is_current", true), ("is_inferred", false));

    private static Row Order(string id, string cust, DateTimeOffset ts, string ccy) =>
        Row.Of(("order_id", id), ("customer_id", cust), ("order_ts", ts),
            ("order_date", ts.UtcDateTime.ToString("yyyy-MM-dd")), ("channel", "web"), ("currency", ccy), ("status", "ok"));

    private static Row Line(string id, string order, string prod, long qty, decimal price, decimal net, string ccy) =>
        Row.Of(("order_line_id", id), ("order_id", order), ("product_id", prod), ("quantity", qty),
            ("unit_price", price), ("discount", 0m), ("currency", ccy), ("gross_amount", net), ("net_amount", net));

    [Fact]
    public void Fact_joins_dimension_version_effective_at_order_time()
    {
        using var lh = new TempLake();
        Seed(lh, Tables.DimCustomer, LakehouseModel.DimCustomer,
            Cust(1, "C1", T0, T1, "retail", false),   // version 1
            Cust(2, "C1", T1, null, "vip", true));     // version 2
        Seed(lh, Tables.DimProduct, LakehouseModel.DimProduct, Prod(100, "P1"));
        Seed(lh, Tables.SilverOrders, LakehouseModel.SilverOrders,
            Order("O1", "C1", T0.AddDays(14), "USD"),  // in v1 window
            Order("O2", "C1", T1.AddDays(14), "USD")); // in v2 window
        Seed(lh, Tables.SilverOrderLines, LakehouseModel.SilverOrderLines,
            Line("L1", "O1", "P1", 1, 100m, 100m, "USD"),
            Line("L2", "O2", "P1", 1, 100m, 100m, "USD"));
        Seed(lh, Tables.SilverFx, LakehouseModel.SilverFx);

        new GoldBuilder(lh.Lake).BuildFactOrderLine();

        var facts = lh.Lake.Table(Tables.FactOrderLine).Scan().ToDictionary(r => r.GetString("order_line_id")!, r => r);
        Assert.Equal(1L, facts["L1"].GetLong("customer_sk")); // retail-era version, NOT the current one
        Assert.Equal(2L, facts["L2"].GetLong("customer_sk")); // vip-era version
    }

    [Fact]
    public void Late_arriving_customer_is_inferred_into_the_dimension()
    {
        using var lh = new TempLake();
        Seed(lh, Tables.DimCustomer, LakehouseModel.DimCustomer);           // empty dimension
        Seed(lh, Tables.DimProduct, LakehouseModel.DimProduct, Prod(100, "P1"));
        Seed(lh, Tables.SilverOrders, LakehouseModel.SilverOrders, Order("O1", "GHOST", T0.AddDays(5), "USD"));
        Seed(lh, Tables.SilverOrderLines, LakehouseModel.SilverOrderLines, Line("L1", "O1", "P1", 1, 100m, 100m, "USD"));
        Seed(lh, Tables.SilverFx, LakehouseModel.SilverFx);

        new GoldBuilder(lh.Lake).BuildFactOrderLine();

        var fact = lh.Lake.Table(Tables.FactOrderLine).Scan().Single();
        var dim = lh.Lake.Table(Tables.DimCustomer).Scan();
        var inferred = dim.Single(r => r.GetBool("is_inferred") == true);
        Assert.Equal("GHOST", inferred.GetString("customer_id"));
        Assert.Equal(inferred.GetLong("customer_sk"), fact.GetLong("customer_sk")); // fact resolves to inferred member
    }

    [Fact]
    public void Net_amount_is_normalised_to_usd_via_fx()
    {
        using var lh = new TempLake();
        Seed(lh, Tables.DimCustomer, LakehouseModel.DimCustomer, Cust(1, "C1", T0, null, "retail", true));
        Seed(lh, Tables.DimProduct, LakehouseModel.DimProduct, Prod(100, "P1"));
        Seed(lh, Tables.SilverOrders, LakehouseModel.SilverOrders, Order("O1", "C1", T0.AddDays(5), "KES"));
        Seed(lh, Tables.SilverOrderLines, LakehouseModel.SilverOrderLines, Line("L1", "O1", "P1", 1, 1000m, 1000m, "KES"));
        Seed(lh, Tables.SilverFx, LakehouseModel.SilverFx,
            Row.Of(("currency", "KES"), ("rate_date", "2026-01-01"), ("rate_to_usd", 0.0077m)));

        new GoldBuilder(lh.Lake).BuildFactOrderLine();

        var fact = lh.Lake.Table(Tables.FactOrderLine).Scan().Single();
        Assert.Equal(1000m, fact.GetDecimal("net_amount"));
        Assert.Equal(7.7m, fact.GetDecimal("net_amount_usd"));
    }

    [Fact]
    public void Daily_revenue_aggregate_sums_fact_usd()
    {
        using var lh = new TempLake();
        Seed(lh, Tables.DimCustomer, LakehouseModel.DimCustomer, Cust(1, "C1", T0, null, "retail", true));
        Seed(lh, Tables.DimProduct, LakehouseModel.DimProduct, Prod(100, "P1"));
        Seed(lh, Tables.SilverOrders, LakehouseModel.SilverOrders, Order("O1", "C1", T0.AddDays(5), "USD"));
        Seed(lh, Tables.SilverOrderLines, LakehouseModel.SilverOrderLines,
            Line("L1", "O1", "P1", 1, 100m, 100m, "USD"),
            Line("L2", "O1", "P1", 2, 100m, 200m, "USD"));
        Seed(lh, Tables.SilverFx, LakehouseModel.SilverFx);

        var gold = new GoldBuilder(lh.Lake);
        gold.BuildFactOrderLine();
        gold.BuildAggDailyRevenue();

        var agg = lh.Lake.Table(Tables.AggDailyRevenue).Scan().Single();
        Assert.Equal(300m, agg.GetDecimal("revenue_usd"));
        Assert.Equal(2L, agg.GetLong("lines"));
    }
}
