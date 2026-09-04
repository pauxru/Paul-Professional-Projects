namespace Lakehouse.Application.Model;

/// <summary>Canonical dataset names across the medallion layers. Referenced by pipelines and lineage.</summary>
public static class Tables
{
    // ---- source entity names (also used as bronze suffixes) ----
    public const string Customers = "customers";
    public const string Products = "products";
    public const string Orders = "orders";
    public const string OrderLines = "order_lines";
    public const string Clickstream = "clickstream";
    public const string Inventory = "inventory_movements";
    public const string Fx = "fx_rates";

    // ---- bronze ----
    public const string BronzeCustomers = "bronze_customers";
    public const string BronzeProducts = "bronze_products";
    public const string BronzeOrders = "bronze_orders";
    public const string BronzeOrderLines = "bronze_order_lines";
    public const string BronzeClickstream = "bronze_clickstream";
    public const string BronzeInventory = "bronze_inventory_movements";
    public const string BronzeFx = "bronze_fx_rates";

    // ---- silver ----
    public const string SilverCustomers = "silver_customers";           // SCD2 dimension
    public const string SilverProducts = "silver_products";             // SCD2 dimension
    public const string SilverOrders = "silver_orders";
    public const string SilverOrderLines = "silver_order_lines";
    public const string SilverClickstream = "silver_clickstream";
    public const string SilverFx = "silver_fx_rates";
    public const string Quarantine = "silver_quarantine";               // rejected rows + reasons

    // ---- gold (star schema) ----
    public const string DimCustomer = "dim_customer";
    public const string DimProduct = "dim_product";
    public const string DimDate = "dim_date";
    public const string DimCurrency = "dim_currency";
    public const string DimChannel = "dim_channel";
    public const string FactOrderLine = "fact_order_line";
    public const string FactClickstreamSession = "fact_clickstream_session";
    public const string AggDailyRevenue = "agg_daily_revenue";
    public const string AggCohortRetention = "agg_cohort_retention";
    public const string AggFunnel = "agg_funnel";
    public const string AggInventoryPosition = "agg_inventory_position";

    public static readonly IReadOnlyList<string> GoldServingTables = new[]
    {
        DimCustomer, DimProduct, DimDate, DimCurrency, DimChannel,
        FactOrderLine, FactClickstreamSession,
        AggDailyRevenue, AggCohortRetention, AggFunnel, AggInventoryPosition
    };

    public static string BronzeFor(string entity) => entity switch
    {
        Customers => BronzeCustomers,
        Products => BronzeProducts,
        Orders => BronzeOrders,
        OrderLines => BronzeOrderLines,
        Clickstream => BronzeClickstream,
        Inventory => BronzeInventory,
        Fx => BronzeFx,
        _ => throw new ArgumentOutOfRangeException(nameof(entity), entity, "Unknown source entity.")
    };
}
