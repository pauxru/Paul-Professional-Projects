using System.Globalization;
using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Model;
using Lakehouse.Domain.Data;
using Lakehouse.Domain.Scd;
using Lakehouse.Domain.Schemas;

namespace Lakehouse.Application.Pipelines;

/// <summary>
/// Gold curation: a star schema (fact + conformed dimensions) plus incremental aggregate marts. Facts
/// join to the dimension version effective at the event time (correct SCD2 semantics), infer late-
/// arriving dimension members, and normalise currency to USD via the FX dimension. Every build is a
/// deterministic rebuild from silver, so re-running is idempotent.
/// </summary>
public sealed class GoldBuilder(ILakehouse lake)
{
    private static readonly DateTimeOffset Epoch = DateTimeOffset.MinValue;

    // ---- conformed dimensions -------------------------------------------------------------------

    public StepResult BuildDimCustomer()
    {
        var rows = Scan(Tables.SilverCustomers).Select(v =>
        {
            var row = v.Clone();
            row["customer_sk"] = v.GetLong("surrogate_key");
            row["is_inferred"] = false;
            return row.Without("surrogate_key");
        }).ToList();
        Table(Tables.DimCustomer, LakehouseModel.DimCustomer).Overwrite(rows);
        return new StepResult("gold:dim_customer", rows.Count, rows.Count);
    }

    public StepResult BuildDimProduct()
    {
        var rows = Scan(Tables.SilverProducts).Select(v =>
        {
            var row = v.Clone();
            row["product_sk"] = v.GetLong("surrogate_key");
            row["is_inferred"] = false;
            return row.Without("surrogate_key");
        }).ToList();
        Table(Tables.DimProduct, LakehouseModel.DimProduct).Overwrite(rows);
        return new StepResult("gold:dim_product", rows.Count, rows.Count);
    }

    public StepResult BuildDimDate()
    {
        var dates = new SortedSet<DateTime>();
        foreach (var d in Scan(Tables.SilverOrders).Select(r => r.GetString("order_date"))) Add(dates, d);
        foreach (var d in Scan(Tables.SilverClickstream).Select(r => r.GetString("event_date"))) Add(dates, d);
        foreach (var d in Scan(Tables.SilverFx).Select(r => r.GetString("rate_date"))) Add(dates, d);

        var rows = new List<Row>();
        if (dates.Count > 0)
        {
            for (var day = dates.Min; day <= dates.Max; day = day.AddDays(1))
            {
                rows.Add(Row.Of(
                    ("date_key", (long)DateKey(day)), ("date", day.ToString("yyyy-MM-dd")),
                    ("year", (long)day.Year), ("quarter", (long)((day.Month - 1) / 3 + 1)),
                    ("month", (long)day.Month), ("day", (long)day.Day),
                    ("day_of_week", (long)day.DayOfWeek), ("day_name", day.DayOfWeek.ToString()),
                    ("is_weekend", day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)));
            }
        }
        Table(Tables.DimDate, LakehouseModel.DimDate).Overwrite(rows);
        return new StepResult("gold:dim_date", rows.Count, rows.Count);

        static void Add(SortedSet<DateTime> set, string? s)
        {
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) set.Add(dt.Date);
        }
    }

    public StepResult BuildDimCurrency()
    {
        var latest = Scan(Tables.SilverFx)
            .GroupBy(r => r.GetString("currency"), StringComparer.Ordinal)
            .Select(g => g.OrderBy(r => r.GetString("rate_date"), StringComparer.Ordinal).Last())
            .Select(r => Row.Of(
                ("currency", r.GetString("currency")),
                ("rate_to_usd", r.GetDecimal("rate_to_usd") ?? 1m),
                ("is_base", r.GetString("currency") == "USD")))
            .ToList();
        if (latest.All(r => r.GetString("currency") != "USD"))
            latest.Add(Row.Of(("currency", "USD"), ("rate_to_usd", 1m), ("is_base", true)));
        Table(Tables.DimCurrency, LakehouseModel.DimCurrency).Overwrite(latest);
        return new StepResult("gold:dim_currency", latest.Count, latest.Count);
    }

    public StepResult BuildDimChannel()
    {
        var channels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Scan(Tables.SilverOrders).Select(r => r.GetString("channel"))) if (!Parsing.IsBlank(c)) channels.Add(c!);
        foreach (var c in Scan(Tables.SilverClickstream).Select(r => r.GetString("channel"))) if (!Parsing.IsBlank(c)) channels.Add(c!);
        var rows = channels.OrderBy(c => c, StringComparer.Ordinal)
            .Select(c => Row.Of(("channel", c), ("description", $"{char.ToUpperInvariant(c[0])}{c[1..]} channel")))
            .ToList();
        Table(Tables.DimChannel, LakehouseModel.DimChannel).Overwrite(rows);
        return new StepResult("gold:dim_channel", rows.Count, rows.Count);
    }

    // ---- facts ----------------------------------------------------------------------------------

    public StepResult BuildFactOrderLine()
    {
        var lines = Scan(Tables.SilverOrderLines);
        var orders = Scan(Tables.SilverOrders).ToDictionary(r => r.GetString("order_id")!, r => r, StringComparer.Ordinal);
        var customers = Scan(Tables.DimCustomer);
        var products = Scan(Tables.DimProduct);
        var fx = new FxBook(Scan(Tables.SilverFx));

        var inferredCustomers = new Dictionary<long, Row>();
        var inferredProducts = new Dictionary<long, Row>();
        var facts = new List<Row>();

        foreach (var line in lines)
        {
            var orderId = line.GetString("order_id")!;
            if (!orders.TryGetValue(orderId, out var order)) continue; // silver RI guarantees presence
            var orderTs = order.GetTimestamp("order_ts") ?? Epoch;
            var customerId = order.GetString("customer_id")!;
            var productId = line.GetString("product_id")!;

            var customerSk = ResolveSk(customers, "customer_id", customerId, orderTs, inferredCustomers, InferCustomer);
            var productSk = ResolveSk(products, "product_id", productId, orderTs, inferredProducts, InferProduct);

            var currency = line.GetString("currency")!;
            var net = line.GetDecimal("net_amount") ?? 0m;
            var netUsd = Math.Round(net * fx.Rate(currency, orderTs.UtcDateTime), 6);

            facts.Add(Row.Of(
                ("order_line_id", line.GetString("order_line_id")), ("order_id", orderId),
                ("order_date_key", (long)DateKey(orderTs.UtcDateTime)),
                ("customer_sk", customerSk), ("product_sk", productSk),
                ("channel", order.GetString("channel")), ("currency", currency),
                ("quantity", line.GetLong("quantity") ?? 0), ("unit_price", line.GetDecimal("unit_price") ?? 0m),
                ("discount", line.GetDecimal("discount") ?? 0m),
                ("gross_amount", line.GetDecimal("gross_amount") ?? 0m), ("net_amount", net),
                ("net_amount_usd", netUsd)));
        }

        Table(Tables.FactOrderLine, LakehouseModel.FactOrderLine).Overwrite(facts);
        if (inferredCustomers.Count > 0) lake.Table(Tables.DimCustomer).Merge(inferredCustomers.Values.ToList(), new[] { "customer_sk" });
        if (inferredProducts.Count > 0) lake.Table(Tables.DimProduct).Merge(inferredProducts.Values.ToList(), new[] { "product_sk" });

        return new StepResult("gold:fact_order_line", lines.Count, facts.Count,
            Note: $"{inferredCustomers.Count} inferred customers, {inferredProducts.Count} inferred products");
    }

    public StepResult BuildFactClickstreamSession()
    {
        var events = Scan(Tables.SilverClickstream);
        var customers = Scan(Tables.DimCustomer);
        var inferred = new Dictionary<long, Row>();

        var facts = new List<Row>();
        foreach (var g in events.GroupBy(e => e.GetString("session_id"), StringComparer.Ordinal))
        {
            var ordered = g.OrderBy(e => e.GetTimestamp("event_ts") ?? Epoch).ToList();
            var start = ordered.First().GetTimestamp("event_ts") ?? Epoch;
            var end = ordered.Last().GetTimestamp("event_ts") ?? Epoch;
            var customerId = ordered.Select(e => e.GetString("customer_id")).FirstOrDefault(c => !Parsing.IsBlank(c));
            var channel = ordered.Select(e => e.GetString("channel")).FirstOrDefault(c => !Parsing.IsBlank(c));

            long Count(params string[] types) => ordered.Count(e => types.Contains(e.GetString("event_type")));
            var purchases = Count("purchase");

            long customerSk;
            if (Parsing.IsBlank(customerId)) customerSk = UnknownCustomer(inferred);
            else customerSk = ResolveSk(customers, "customer_id", customerId!, start, inferred, InferCustomer);

            facts.Add(Row.Of(
                ("session_id", g.Key), ("customer_sk", customerSk),
                ("session_date_key", (long)DateKey(start.UtcDateTime)), ("channel", channel),
                ("start_ts", start), ("end_ts", end), ("event_count", (long)ordered.Count),
                ("page_views", Count("view")), ("add_to_carts", Count("add_to_cart")),
                ("checkouts", Count("checkout")), ("purchases", purchases), ("converted", purchases > 0)));
        }

        Table(Tables.FactClickstreamSession, LakehouseModel.FactClickstreamSession).Overwrite(facts);
        if (inferred.Count > 0) lake.Table(Tables.DimCustomer).Merge(inferred.Values.ToList(), new[] { "customer_sk" });
        return new StepResult("gold:fact_clickstream_session", events.Count, facts.Count);
    }

    // ---- aggregate marts ------------------------------------------------------------------------

    public StepResult BuildAggDailyRevenue()
    {
        var rows = Scan(Tables.FactOrderLine)
            .GroupBy(f => f.GetLong("order_date_key") ?? 0)
            .OrderBy(g => g.Key)
            .Select(g => Row.Of(
                ("date_key", g.Key),
                ("orders", (long)g.Select(f => f.GetString("order_id")).Distinct(StringComparer.Ordinal).Count()),
                ("lines", (long)g.Count()),
                ("units", g.Sum(f => f.GetLong("quantity") ?? 0)),
                ("revenue_usd", Math.Round(g.Sum(f => f.GetDecimal("net_amount_usd") ?? 0m), 2))))
            .ToList();
        Table(Tables.AggDailyRevenue, LakehouseModel.AggDailyRevenue).Overwrite(rows);
        return new StepResult("gold:agg_daily_revenue", rows.Count, rows.Count);
    }

    public StepResult BuildAggCohortRetention()
    {
        // Cohort = month of a customer's first order; retention counts distinct customers active N months later.
        var orders = Scan(Tables.SilverOrders)
            .Select(r => (Customer: r.GetString("customer_id"), Ts: r.GetTimestamp("order_ts")))
            .Where(x => !Parsing.IsBlank(x.Customer) && x.Ts is not null)
            .Select(x => (x.Customer!, Month: new DateTime(x.Ts!.Value.Year, x.Ts.Value.Month, 1)))
            .ToList();

        var cohortByCustomer = orders.GroupBy(o => o.Item1, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Min(o => o.Month), StringComparer.Ordinal);

        var rows = orders
            .Select(o => (Cohort: cohortByCustomer[o.Item1], Activity: o.Month, Customer: o.Item1))
            .Distinct()
            .GroupBy(x => (x.Cohort, x.Activity))
            .Select(g => Row.Of(
                ("cohort_month", g.Key.Cohort.ToString("yyyy-MM")),
                ("activity_month", g.Key.Activity.ToString("yyyy-MM")),
                ("months_since", (long)((g.Key.Activity.Year - g.Key.Cohort.Year) * 12 + g.Key.Activity.Month - g.Key.Cohort.Month)),
                ("customers", (long)g.Select(x => x.Customer).Distinct(StringComparer.Ordinal).Count())))
            .OrderBy(r => r.GetString("cohort_month"), StringComparer.Ordinal)
            .ThenBy(r => r.GetLong("months_since"))
            .ToList();
        Table(Tables.AggCohortRetention, LakehouseModel.AggCohortRetention).Overwrite(rows);
        return new StepResult("gold:agg_cohort_retention", orders.Count, rows.Count);
    }

    public StepResult BuildAggFunnel()
    {
        var sessions = Scan(Tables.FactClickstreamSession);
        long Step(Func<Row, bool> p) => sessions.Count(p);
        var rows = new List<Row>
        {
            Row.Of(("step_order", 1L), ("step", "sessions"), ("sessions", (long)sessions.Count)),
            Row.Of(("step_order", 2L), ("step", "product_view"), ("sessions", Step(s => (s.GetLong("page_views") ?? 0) > 0))),
            Row.Of(("step_order", 3L), ("step", "add_to_cart"), ("sessions", Step(s => (s.GetLong("add_to_carts") ?? 0) > 0))),
            Row.Of(("step_order", 4L), ("step", "checkout"), ("sessions", Step(s => (s.GetLong("checkouts") ?? 0) > 0))),
            Row.Of(("step_order", 5L), ("step", "purchase"), ("sessions", Step(s => (s.GetLong("purchases") ?? 0) > 0)))
        };
        Table(Tables.AggFunnel, LakehouseModel.AggFunnel).Overwrite(rows);
        return new StepResult("gold:agg_funnel", sessions.Count, rows.Count);
    }

    public StepResult BuildAggInventoryPosition()
    {
        var bronze = Scan(Tables.BronzeInventory);
        var byProduct = bronze
            .Where(r => r.GetString(Meta.Op) != "D")
            .GroupBy(r => r.GetString("product_id"), StringComparer.Ordinal)
            .Where(g => !Parsing.IsBlank(g.Key));

        var rows = new List<Row>();
        foreach (var g in byProduct)
        {
            long position = 0;
            string maxDate = "";
            foreach (var r in g)
            {
                if (Parsing.TryLong(r.GetString("delta_qty"), out var delta)) position += delta;
                if (Parsing.TryTimestampUtc(r.GetString("event_ts"), out var ts))
                {
                    var d = ts.UtcDateTime.ToString("yyyy-MM-dd");
                    if (string.CompareOrdinal(d, maxDate) > 0) maxDate = d;
                }
            }
            rows.Add(Row.Of(("product_id", g.Key), ("position", position), ("as_of_date", maxDate)));
        }
        rows = rows.OrderBy(r => r.GetString("product_id"), StringComparer.Ordinal).ToList();
        Table(Tables.AggInventoryPosition, LakehouseModel.AggInventoryPosition).Overwrite(rows);
        return new StepResult("gold:agg_inventory_position", bronze.Count, rows.Count);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private static long ResolveSk(IReadOnlyList<Row> versions, string keyCol, string businessKey,
        DateTimeOffset eventTs, Dictionary<long, Row> inferred, Func<string, Row> infer)
    {
        var match = DimensionResolver.Effective(versions, keyCol, businessKey, eventTs);
        if (match is not null) return match.GetLong(keyCol == "customer_id" ? "customer_sk" : "product_sk") ?? 0;

        var member = infer(businessKey);
        var sk = member.GetLong(keyCol == "customer_id" ? "customer_sk" : "product_sk") ?? 0;
        inferred[sk] = member;
        return sk;
    }

    private static Row InferCustomer(string customerId) => Row.Of(
        ("customer_sk", Scd2Processor.SurrogateKey(customerId, Epoch)), ("customer_id", customerId),
        ("valid_from", Epoch), ("valid_to", (object?)null), ("is_current", true), ("is_inferred", true));

    private static Row InferProduct(string productId) => Row.Of(
        ("product_sk", Scd2Processor.SurrogateKey(productId, Epoch)), ("product_id", productId),
        ("valid_from", Epoch), ("valid_to", (object?)null), ("is_current", true), ("is_inferred", true));

    private static long UnknownCustomer(Dictionary<long, Row> inferred)
    {
        var member = Row.Of(
            ("customer_sk", 0L), ("customer_id", "UNKNOWN"), ("valid_from", Epoch),
            ("valid_to", (object?)null), ("is_current", true), ("is_inferred", true));
        inferred[0L] = member;
        return 0L;
    }

    private static int DateKey(DateTime day) => int.Parse(day.ToString("yyyyMMdd", CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

    private IReadOnlyList<Row> Scan(string table)
    {
        var t = lake.Table(table);
        return t.Exists ? t.Scan() : Array.Empty<Row>();
    }

    private ILakeTable Table(string name, Func<TableSchema> schema)
    {
        var t = lake.Table(name);
        if (!t.Exists) t.Create(schema());
        return t;
    }

    /// <summary>Per-currency FX lookup with as-of-date semantics (most recent rate on or before the date).</summary>
    private sealed class FxBook
    {
        private readonly Dictionary<string, List<(string Date, decimal Rate)>> _byCcy;

        public FxBook(IReadOnlyList<Row> fxRows)
        {
            _byCcy = fxRows
                .GroupBy(r => r.GetString("currency")!, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(r => (Date: r.GetString("rate_date") ?? "", Rate: r.GetDecimal("rate_to_usd") ?? 1m))
                          .OrderBy(x => x.Date, StringComparer.Ordinal).ToList(),
                    StringComparer.Ordinal);
        }

        public decimal Rate(string currency, DateTime asOf)
        {
            if (currency == "USD") return 1m;
            if (!_byCcy.TryGetValue(currency, out var list) || list.Count == 0) return 1m; // unknown currency → identity
            var date = asOf.ToString("yyyy-MM-dd");
            decimal rate = list[0].Rate;
            foreach (var (d, r) in list)
            {
                if (string.CompareOrdinal(d, date) <= 0) rate = r;
                else break;
            }
            return rate;
        }
    }
}
