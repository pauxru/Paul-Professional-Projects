using Lakehouse.Application.Model;
using Lakehouse.Domain.Lineage;

namespace Lakehouse.Application.Lineage;

/// <summary>
/// The column-level lineage of the whole platform, declared from the transformations that the pipelines
/// actually perform. This is the single source of truth for provenance and impact analysis: every edge
/// here corresponds to a computation in <c>BronzeIngestor</c>, <c>SilverBuilder</c> or <c>GoldBuilder</c>.
/// Because it is code (not a hand-drawn diagram) it can be queried, rendered to Mermaid, and — crucially —
/// used to answer "if this source column changes, what breaks?".
/// </summary>
public static class LineageCatalog
{
    public static LineageGraph Build()
    {
        var g = new LineageGraph();

        // 1) sources -> bronze : append-only ingest, 1:1 passthrough (values kept raw + stamped).
        BronzePassthrough(g, Tables.Customers, Tables.BronzeCustomers, CustomerBusiness);
        BronzePassthrough(g, Tables.Products, Tables.BronzeProducts, LakehouseModel.ProductColumns);
        BronzePassthrough(g, Tables.Orders, Tables.BronzeOrders, LakehouseModel.OrderColumns);
        BronzePassthrough(g, Tables.OrderLines, Tables.BronzeOrderLines, LakehouseModel.OrderLineColumns);
        BronzePassthrough(g, Tables.Clickstream, Tables.BronzeClickstream, LakehouseModel.ClickstreamColumns);
        BronzePassthrough(g, Tables.Inventory, Tables.BronzeInventory, LakehouseModel.InventoryColumns);
        BronzePassthrough(g, Tables.Fx, Tables.BronzeFx, LakehouseModel.FxColumns);

        // 2) bronze -> silver.
        SilverCustomers(g);
        SilverProducts(g);
        SilverOrders(g);
        SilverOrderLines(g);
        SilverClickstream(g);
        SilverFx(g);

        // 3) silver -> gold (dims + facts).
        GoldDimCustomer(g);
        GoldDimProduct(g);
        GoldFactOrderLine(g);
        GoldFactClickstream(g);

        // 4) gold facts -> aggregate marts.
        GoldAggregates(g);

        return g;
    }

    private static readonly IReadOnlyList<string> CustomerBusiness = new[]
    {
        "customer_id", "name", "email", "city", "country", "currency", "segment",
        "created_at", "updated_at", LakehouseModel.CustomerEvolvedColumn
    };

    private static string Src(string entity) => $"source_{entity}";

    private static void BronzePassthrough(LineageGraph g, string entity, string bronze, IReadOnlyList<string> cols)
    {
        var src = Src(entity);
        foreach (var col in cols)
            g.Add(new ColumnRef(bronze, col), "ingest (append-only, source-stamped)", new ColumnRef(src, col));
    }

    private static void SilverCustomers(LineageGraph g)
    {
        const string b = Tables.BronzeCustomers, s = Tables.SilverCustomers;
        g.Add(new ColumnRef(s, "surrogate_key"), "hash(customer_id | valid_from)",
            new ColumnRef(b, "customer_id"), new ColumnRef(b, Meta.CommitTs));
        g.Add(new ColumnRef(s, "customer_id"), "typed passthrough", new ColumnRef(b, "customer_id"));
        foreach (var col in LakehouseModel.CustomerTracked)
            g.Add(new ColumnRef(s, col), "SCD2 tracked attribute", new ColumnRef(b, col));
        g.Add(new ColumnRef(s, "valid_from"), "SCD2 valid_from = commit_ts", new ColumnRef(b, Meta.CommitTs));
        g.Add(new ColumnRef(s, "valid_to"), "SCD2 valid_to = next version commit_ts", new ColumnRef(b, Meta.CommitTs));
        g.Add(new ColumnRef(s, "is_current"), "SCD2 open-segment flag", new ColumnRef(b, Meta.CommitTs));
    }

    private static void SilverProducts(LineageGraph g)
    {
        const string b = Tables.BronzeProducts, s = Tables.SilverProducts;
        g.Add(new ColumnRef(s, "surrogate_key"), "hash(product_id | valid_from)",
            new ColumnRef(b, "product_id"), new ColumnRef(b, Meta.CommitTs));
        g.Add(new ColumnRef(s, "product_id"), "typed passthrough", new ColumnRef(b, "product_id"));
        foreach (var col in LakehouseModel.ProductTracked)
            g.Add(new ColumnRef(s, col), "SCD2 tracked attribute (typed)", new ColumnRef(b, col));
        g.Add(new ColumnRef(s, "valid_from"), "SCD2 valid_from = commit_ts", new ColumnRef(b, Meta.CommitTs));
        g.Add(new ColumnRef(s, "valid_to"), "SCD2 valid_to = next version commit_ts", new ColumnRef(b, Meta.CommitTs));
        g.Add(new ColumnRef(s, "is_current"), "SCD2 open-segment flag", new ColumnRef(b, Meta.CommitTs));
    }

    private static void SilverOrders(LineageGraph g)
    {
        const string b = Tables.BronzeOrders, s = Tables.SilverOrders;
        g.Add(new ColumnRef(s, "order_id"), "dedup by business key + sequence", new ColumnRef(b, "order_id"));
        g.Add(new ColumnRef(s, "customer_id"), "referential-integrity conformance", new ColumnRef(b, "customer_id"));
        g.Add(new ColumnRef(s, "order_ts"), "parse timestamp → UTC (tz normalise)", new ColumnRef(b, "order_ts"));
        g.Add(new ColumnRef(s, "order_date"), "date(order_ts)", new ColumnRef(b, "order_ts"));
        g.Add(new ColumnRef(s, "channel"), "typed passthrough", new ColumnRef(b, "channel"));
        g.Add(new ColumnRef(s, "currency"), "typed passthrough", new ColumnRef(b, "currency"));
        g.Add(new ColumnRef(s, "status"), "typed passthrough", new ColumnRef(b, "status"));
    }

    private static void SilverOrderLines(LineageGraph g)
    {
        const string b = Tables.BronzeOrderLines, s = Tables.SilverOrderLines;
        g.Add(new ColumnRef(s, "order_line_id"), "dedup by business key + sequence", new ColumnRef(b, "order_line_id"));
        g.Add(new ColumnRef(s, "order_id"), "referential-integrity conformance", new ColumnRef(b, "order_id"));
        g.Add(new ColumnRef(s, "product_id"), "referential-integrity conformance", new ColumnRef(b, "product_id"));
        g.Add(new ColumnRef(s, "quantity"), "parse long (> 0)", new ColumnRef(b, "quantity"));
        g.Add(new ColumnRef(s, "unit_price"), "parse decimal (>= 0)", new ColumnRef(b, "unit_price"));
        g.Add(new ColumnRef(s, "discount"), "parse decimal (>= 0)", new ColumnRef(b, "discount"));
        g.Add(new ColumnRef(s, "currency"), "typed passthrough", new ColumnRef(b, "currency"));
        g.Add(new ColumnRef(s, "gross_amount"), "quantity * unit_price",
            new ColumnRef(b, "quantity"), new ColumnRef(b, "unit_price"));
        g.Add(new ColumnRef(s, "net_amount"), "quantity * unit_price - discount",
            new ColumnRef(b, "quantity"), new ColumnRef(b, "unit_price"), new ColumnRef(b, "discount"));
    }

    private static void SilverClickstream(LineageGraph g)
    {
        const string b = Tables.BronzeClickstream, s = Tables.SilverClickstream;
        foreach (var col in new[] { "event_id", "session_id", "customer_id", "page", "event_type", "channel" })
            g.Add(new ColumnRef(s, col), "typed passthrough / dedup", new ColumnRef(b, col));
        g.Add(new ColumnRef(s, "event_ts"), "parse timestamp → UTC", new ColumnRef(b, "event_ts"));
        g.Add(new ColumnRef(s, "event_date"), "date(event_ts)", new ColumnRef(b, "event_ts"));
    }

    private static void SilverFx(LineageGraph g)
    {
        const string b = Tables.BronzeFx, s = Tables.SilverFx;
        g.Add(new ColumnRef(s, "currency"), "typed passthrough", new ColumnRef(b, "currency"));
        g.Add(new ColumnRef(s, "rate_date"), "typed passthrough", new ColumnRef(b, "rate_date"));
        g.Add(new ColumnRef(s, "rate_to_usd"), "parse decimal (> 0)", new ColumnRef(b, "rate_to_usd"));
    }

    private static void GoldDimCustomer(LineageGraph g)
    {
        const string s = Tables.SilverCustomers, d = Tables.DimCustomer;
        g.Add(new ColumnRef(d, "customer_sk"), "conform surrogate key", new ColumnRef(s, "surrogate_key"));
        foreach (var col in new[] { "customer_id", "name", "email", "city", "country", "currency", "segment",
                     "loyalty_tier", "valid_from", "valid_to", "is_current" })
            g.Add(new ColumnRef(d, col), "conform dimension", new ColumnRef(s, col));
    }

    private static void GoldDimProduct(LineageGraph g)
    {
        const string s = Tables.SilverProducts, d = Tables.DimProduct;
        g.Add(new ColumnRef(d, "product_sk"), "conform surrogate key", new ColumnRef(s, "surrogate_key"));
        foreach (var col in new[] { "product_id", "name", "category", "unit_price", "currency", "active",
                     "valid_from", "valid_to", "is_current" })
            g.Add(new ColumnRef(d, col), "conform dimension", new ColumnRef(s, col));
    }

    private static void GoldFactOrderLine(LineageGraph g)
    {
        const string sl = Tables.SilverOrderLines, so = Tables.SilverOrders, f = Tables.FactOrderLine;
        g.Add(new ColumnRef(f, "order_line_id"), "passthrough", new ColumnRef(sl, "order_line_id"));
        g.Add(new ColumnRef(f, "order_id"), "passthrough", new ColumnRef(sl, "order_id"));
        g.Add(new ColumnRef(f, "order_date_key"), "yyyymmdd(order_ts)", new ColumnRef(so, "order_ts"));
        g.Add(new ColumnRef(f, "customer_sk"), "SCD2 effective-version join at order_ts",
            new ColumnRef(Tables.DimCustomer, "customer_sk"), new ColumnRef(so, "order_ts"), new ColumnRef(so, "customer_id"));
        g.Add(new ColumnRef(f, "product_sk"), "SCD2 effective-version join at order_ts",
            new ColumnRef(Tables.DimProduct, "product_sk"), new ColumnRef(so, "order_ts"), new ColumnRef(sl, "product_id"));
        g.Add(new ColumnRef(f, "channel"), "passthrough", new ColumnRef(so, "channel"));
        g.Add(new ColumnRef(f, "currency"), "passthrough", new ColumnRef(sl, "currency"));
        g.Add(new ColumnRef(f, "quantity"), "passthrough", new ColumnRef(sl, "quantity"));
        g.Add(new ColumnRef(f, "unit_price"), "passthrough", new ColumnRef(sl, "unit_price"));
        g.Add(new ColumnRef(f, "discount"), "passthrough", new ColumnRef(sl, "discount"));
        g.Add(new ColumnRef(f, "gross_amount"), "passthrough", new ColumnRef(sl, "gross_amount"));
        g.Add(new ColumnRef(f, "net_amount"), "passthrough", new ColumnRef(sl, "net_amount"));
        g.Add(new ColumnRef(f, "net_amount_usd"), "net_amount * fx.rate_to_usd (as-of order_date)",
            new ColumnRef(sl, "net_amount"), new ColumnRef(Tables.SilverFx, "rate_to_usd"));
    }

    private static void GoldFactClickstream(LineageGraph g)
    {
        const string s = Tables.SilverClickstream, f = Tables.FactClickstreamSession;
        g.Add(new ColumnRef(f, "session_id"), "group by session", new ColumnRef(s, "session_id"));
        g.Add(new ColumnRef(f, "customer_sk"), "SCD2 effective-version join at session start",
            new ColumnRef(Tables.DimCustomer, "customer_sk"), new ColumnRef(s, "customer_id"), new ColumnRef(s, "event_ts"));
        g.Add(new ColumnRef(f, "session_date_key"), "yyyymmdd(min(event_ts))", new ColumnRef(s, "event_ts"));
        g.Add(new ColumnRef(f, "channel"), "first channel in session", new ColumnRef(s, "channel"));
        g.Add(new ColumnRef(f, "start_ts"), "min(event_ts)", new ColumnRef(s, "event_ts"));
        g.Add(new ColumnRef(f, "end_ts"), "max(event_ts)", new ColumnRef(s, "event_ts"));
        foreach (var col in new[] { "event_count", "page_views", "add_to_carts", "checkouts", "purchases", "converted" })
            g.Add(new ColumnRef(f, col), "count by event_type", new ColumnRef(s, "event_type"));
    }

    private static void GoldAggregates(LineageGraph g)
    {
        const string f = Tables.FactOrderLine;
        g.Add(new ColumnRef(Tables.AggDailyRevenue, "date_key"), "group by order_date_key", new ColumnRef(f, "order_date_key"));
        g.Add(new ColumnRef(Tables.AggDailyRevenue, "orders"), "count distinct order_id", new ColumnRef(f, "order_id"));
        g.Add(new ColumnRef(Tables.AggDailyRevenue, "lines"), "count", new ColumnRef(f, "order_line_id"));
        g.Add(new ColumnRef(Tables.AggDailyRevenue, "units"), "sum(quantity)", new ColumnRef(f, "quantity"));
        g.Add(new ColumnRef(Tables.AggDailyRevenue, "revenue_usd"), "sum(net_amount_usd)", new ColumnRef(f, "net_amount_usd"));

        g.Add(new ColumnRef(Tables.AggCohortRetention, "cohort_month"), "min order month per customer",
            new ColumnRef(Tables.SilverOrders, "customer_id"), new ColumnRef(Tables.SilverOrders, "order_ts"));
        g.Add(new ColumnRef(Tables.AggCohortRetention, "activity_month"), "order month", new ColumnRef(Tables.SilverOrders, "order_ts"));
        g.Add(new ColumnRef(Tables.AggCohortRetention, "customers"), "count distinct customers", new ColumnRef(Tables.SilverOrders, "customer_id"));

        foreach (var step in new[] { "sessions", "step", "step_order" })
            g.Add(new ColumnRef(Tables.AggFunnel, step), "funnel step counts",
                new ColumnRef(Tables.FactClickstreamSession, "purchases"),
                new ColumnRef(Tables.FactClickstreamSession, "page_views"));

        g.Add(new ColumnRef(Tables.AggInventoryPosition, "position"), "sum(delta_qty)", new ColumnRef(Tables.BronzeInventory, "delta_qty"));
        g.Add(new ColumnRef(Tables.AggInventoryPosition, "product_id"), "group by product", new ColumnRef(Tables.BronzeInventory, "product_id"));
        g.Add(new ColumnRef(Tables.AggInventoryPosition, "as_of_date"), "max(event_ts)", new ColumnRef(Tables.BronzeInventory, "event_ts"));
    }
}
