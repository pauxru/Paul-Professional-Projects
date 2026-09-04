using Lakehouse.Domain.Schemas;

namespace Lakehouse.Application.Model;

/// <summary>
/// The canonical schema catalogue for every dataset in the medallion. Bronze stores business columns as
/// raw strings (so silver owns the typed-parse-with-rejection contract); silver and gold are strongly
/// typed. Keeping every schema in one place makes the generator, pipelines, lineage and serving agree.
/// </summary>
public static class LakehouseModel
{
    private const ColumnType Str = ColumnType.String;
    private const ColumnType I64 = ColumnType.Long;
    private const ColumnType Dec = ColumnType.Decimal;
    private const ColumnType Bit = ColumnType.Bool;
    private const ColumnType Ts = ColumnType.Timestamp;

    private static ColumnDef C(string name, ColumnType type, bool nullable = true) => new(name, type, nullable);

    public static readonly IReadOnlyList<string> SourceEntities = new[]
    {
        Tables.Customers, Tables.Products, Tables.Orders, Tables.OrderLines,
        Tables.Clickstream, Tables.Inventory, Tables.Fx
    };

    // Business columns per source entity (order matters; all raw = string at bronze).
    public static readonly IReadOnlyList<string> CustomerColumns = new[]
        { "customer_id", "name", "email", "city", "country", "currency", "segment", "created_at", "updated_at" };
    public const string CustomerEvolvedColumn = "loyalty_tier"; // appears mid-stream (schema evolution)

    public static readonly IReadOnlyList<string> ProductColumns = new[]
        { "product_id", "name", "category", "unit_price", "currency", "active", "created_at", "updated_at" };
    public static readonly IReadOnlyList<string> OrderColumns = new[]
        { "order_id", "customer_id", "order_ts", "channel", "currency", "status", "updated_at" };
    public static readonly IReadOnlyList<string> OrderLineColumns = new[]
        { "order_line_id", "order_id", "product_id", "quantity", "unit_price", "discount", "currency" };
    public static readonly IReadOnlyList<string> ClickstreamColumns = new[]
        { "event_id", "session_id", "customer_id", "event_ts", "page", "event_type", "channel" };
    public static readonly IReadOnlyList<string> InventoryColumns = new[]
        { "movement_id", "product_id", "event_ts", "delta_qty", "reason" };
    public static readonly IReadOnlyList<string> FxColumns = new[]
        { "currency", "rate_date", "rate_to_usd" };

    // SCD2 tracked attributes (a change in any of these opens a new dimension version).
    public static readonly IReadOnlyList<string> CustomerTracked = new[]
        { "name", "email", "city", "country", "currency", "segment", "loyalty_tier" };
    public static readonly IReadOnlyList<string> ProductTracked = new[]
        { "name", "category", "unit_price", "currency", "active" };

    // ---- bronze schemas: business columns as String + metadata ----------------------------------

    private static readonly ColumnDef[] MetaCols =
    {
        C(Meta.Op, Str, false), C(Meta.Sequence, I64, false), C(Meta.CommitTs, Ts, false),
        C(Meta.IngestTs, Ts, false), C(Meta.IngestDate, Str, false), C(Meta.Source, Str, false),
        C(Meta.SourceOffset, I64, false), C(Meta.RunId, Str, false)
    };

    public static TableSchema Bronze(IReadOnlyList<string> businessColumns)
    {
        var cols = businessColumns.Select(c => C(c, Str)).Concat(MetaCols).ToList();
        return new TableSchema(1, cols);
    }

    public static TableSchema BronzeCustomers() => Bronze(CustomerColumns);
    public static TableSchema BronzeProducts() => Bronze(ProductColumns);
    public static TableSchema BronzeOrders() => Bronze(OrderColumns);
    public static TableSchema BronzeOrderLines() => Bronze(OrderLineColumns);
    public static TableSchema BronzeClickstream() => Bronze(ClickstreamColumns);
    public static TableSchema BronzeInventory() => Bronze(InventoryColumns);
    public static TableSchema BronzeFx() => Bronze(FxColumns);

    // ---- silver schemas: typed, cleansed --------------------------------------------------------

    public static TableSchema SilverCustomers() => new(1, new[]
    {
        C("surrogate_key", I64, false), C("customer_id", Str, false),
        C("name", Str), C("email", Str), C("city", Str), C("country", Str),
        C("currency", Str), C("segment", Str), C("loyalty_tier", Str),
        C("valid_from", Ts, false), C("valid_to", Ts), C("is_current", Bit, false)
    }, new[] { "surrogate_key" });

    public static TableSchema SilverProducts() => new(1, new[]
    {
        C("surrogate_key", I64, false), C("product_id", Str, false),
        C("name", Str), C("category", Str), C("unit_price", Dec), C("currency", Str), C("active", Bit),
        C("valid_from", Ts, false), C("valid_to", Ts), C("is_current", Bit, false)
    }, new[] { "surrogate_key" });

    public static TableSchema SilverOrders() => new(1, new[]
    {
        C("order_id", Str, false), C("customer_id", Str, false),
        C("order_ts", Ts, false), C("order_date", Str, false),
        C("channel", Str), C("currency", Str), C("status", Str)
    }, new[] { "order_id" });

    public static TableSchema SilverOrderLines() => new(1, new[]
    {
        C("order_line_id", Str, false), C("order_id", Str, false), C("product_id", Str, false),
        C("quantity", I64, false), C("unit_price", Dec, false), C("discount", Dec),
        C("currency", Str, false), C("gross_amount", Dec, false), C("net_amount", Dec, false)
    }, new[] { "order_line_id" });

    public static TableSchema SilverClickstream() => new(1, new[]
    {
        C("event_id", Str, false), C("session_id", Str, false), C("customer_id", Str),
        C("event_ts", Ts, false), C("event_date", Str, false),
        C("page", Str), C("event_type", Str, false), C("channel", Str)
    }, new[] { "event_id" });

    public static TableSchema SilverFx() => new(1, new[]
    {
        C("currency", Str, false), C("rate_date", Str, false), C("rate_to_usd", Dec, false)
    }, new[] { "currency", "rate_date" });

    public static TableSchema Quarantine() => new(1, new[]
    {
        C("quarantine_id", Str, false), C("source_table", Str, false), C("reason", Str, false),
        C("run_id", Str, false), C("rejected_ts", Ts, false), C("payload", Str, false)
    }, new[] { "quarantine_id" });

    // ---- gold schemas: star ---------------------------------------------------------------------

    public static TableSchema DimCustomer() => new(1, new[]
    {
        C("customer_sk", I64, false), C("customer_id", Str, false),
        C("name", Str), C("email", Str), C("city", Str), C("country", Str),
        C("currency", Str), C("segment", Str), C("loyalty_tier", Str),
        C("valid_from", Ts, false), C("valid_to", Ts), C("is_current", Bit, false), C("is_inferred", Bit, false)
    }, new[] { "customer_sk" });

    public static TableSchema DimProduct() => new(1, new[]
    {
        C("product_sk", I64, false), C("product_id", Str, false),
        C("name", Str), C("category", Str), C("unit_price", Dec), C("currency", Str), C("active", Bit),
        C("valid_from", Ts, false), C("valid_to", Ts), C("is_current", Bit, false), C("is_inferred", Bit, false)
    }, new[] { "product_sk" });

    public static TableSchema DimDate() => new(1, new[]
    {
        C("date_key", I64, false), C("date", Str, false), C("year", I64, false), C("quarter", I64, false),
        C("month", I64, false), C("day", I64, false), C("day_of_week", I64, false),
        C("day_name", Str, false), C("is_weekend", Bit, false)
    }, new[] { "date_key" });

    public static TableSchema DimCurrency() => new(1, new[]
    {
        C("currency", Str, false), C("rate_to_usd", Dec, false), C("is_base", Bit, false)
    }, new[] { "currency" });

    public static TableSchema DimChannel() => new(1, new[]
    {
        C("channel", Str, false), C("description", Str, false)
    }, new[] { "channel" });

    public static TableSchema FactOrderLine() => new(1, new[]
    {
        C("order_line_id", Str, false), C("order_id", Str, false), C("order_date_key", I64, false),
        C("customer_sk", I64, false), C("product_sk", I64, false), C("channel", Str), C("currency", Str, false),
        C("quantity", I64, false), C("unit_price", Dec, false), C("discount", Dec),
        C("gross_amount", Dec, false), C("net_amount", Dec, false), C("net_amount_usd", Dec, false)
    }, new[] { "order_line_id" });

    public static TableSchema FactClickstreamSession() => new(1, new[]
    {
        C("session_id", Str, false), C("customer_sk", I64, false), C("session_date_key", I64, false),
        C("channel", Str), C("start_ts", Ts, false), C("end_ts", Ts, false),
        C("event_count", I64, false), C("page_views", I64, false), C("add_to_carts", I64, false),
        C("checkouts", I64, false), C("purchases", I64, false), C("converted", Bit, false)
    }, new[] { "session_id" });

    public static TableSchema AggDailyRevenue() => new(1, new[]
    {
        C("date_key", I64, false), C("orders", I64, false), C("lines", I64, false),
        C("units", I64, false), C("revenue_usd", Dec, false)
    }, new[] { "date_key" });

    public static TableSchema AggCohortRetention() => new(1, new[]
    {
        C("cohort_month", Str, false), C("activity_month", Str, false),
        C("months_since", I64, false), C("customers", I64, false)
    }, new[] { "cohort_month", "activity_month" });

    public static TableSchema AggFunnel() => new(1, new[]
    {
        C("step_order", I64, false), C("step", Str, false), C("sessions", I64, false)
    }, new[] { "step" });

    public static TableSchema AggInventoryPosition() => new(1, new[]
    {
        C("product_id", Str, false), C("position", I64, false), C("as_of_date", Str, false)
    }, new[] { "product_id" });
}
