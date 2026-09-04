using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Model;
using Lakehouse.Domain.Data;
using Lakehouse.Domain.Scd;
using Lakehouse.Domain.Schemas;

namespace Lakehouse.Application.Pipelines;

/// <summary>
/// Silver transformation: typed parsing with a rejection path (bad rows are quarantined with a reason,
/// never silently dropped), deduplication by business key + sequence, SCD2 on customers and products,
/// referential-integrity conformance, and timezone normalisation. Every build is a deterministic full
/// rebuild from the immutable bronze log, so re-running a window is idempotent by construction. Each
/// build owns its slice of the shared quarantine table (delete-then-append keyed by source), so a
/// re-run replaces — never accumulates — its rejects.
/// </summary>
public sealed class SilverBuilder(ILakehouse lake, IClock clock)
{
    // ---- customers (SCD2) -----------------------------------------------------------------------

    public StepResult BuildCustomers(string runId)
    {
        var bronze = Scan(Tables.BronzeCustomers);
        var rejects = new List<Row>();
        var changes = new List<DimChange>();

        foreach (var r in bronze)
        {
            var seq = r.GetLong(Meta.Sequence) ?? 0;
            var op = r.GetString(Meta.Op);
            var commitTs = r.GetTimestamp(Meta.CommitTs) ?? clock.UtcNow;
            var key = r.GetString("customer_id");

            if (Parsing.IsBlank(key)) { rejects.Add(Reject(Tables.BronzeCustomers, "null customer_id", runId, r)); continue; }
            if (op == "D") { changes.Add(new DimChange(key!, commitTs, seq, null, IsDelete: true)); continue; }
            if (Parsing.IsBlank(r.GetString("name"))) { rejects.Add(Reject(Tables.BronzeCustomers, "null name", runId, r)); continue; }
            if (Parsing.IsBlank(r.GetString("email"))) { rejects.Add(Reject(Tables.BronzeCustomers, "null email", runId, r)); continue; }

            var attrs = new Row { ["customer_id"] = key };
            foreach (var col in LakehouseModel.CustomerTracked) attrs[col] = r.GetString(col);
            changes.Add(new DimChange(key!, commitTs, seq, attrs));
        }

        var versions = Scd2Processor.Build(changes, LakehouseModel.CustomerTracked);
        var rows = versions.Select(v =>
        {
            var row = Row.Of(("surrogate_key", v.SurrogateKey), ("customer_id", v.BusinessKey),
                ("valid_from", v.ValidFrom), ("valid_to", (object?)v.ValidTo), ("is_current", v.IsCurrent));
            foreach (var col in LakehouseModel.CustomerTracked) row[col] = v.Attributes[col];
            return row;
        }).ToList();

        Table(Tables.SilverCustomers, LakehouseModel.SilverCustomers).Overwrite(rows);
        WriteQuarantine(Tables.BronzeCustomers, rejects, runId);
        return new StepResult("silver:customers", bronze.Count, rows.Count, rejects.Count);
    }

    // ---- products (SCD2) ------------------------------------------------------------------------

    public StepResult BuildProducts(string runId)
    {
        var bronze = Scan(Tables.BronzeProducts);
        var rejects = new List<Row>();
        var changes = new List<DimChange>();

        foreach (var r in bronze)
        {
            var seq = r.GetLong(Meta.Sequence) ?? 0;
            var op = r.GetString(Meta.Op);
            var commitTs = r.GetTimestamp(Meta.CommitTs) ?? clock.UtcNow;
            var key = r.GetString("product_id");

            if (Parsing.IsBlank(key)) { rejects.Add(Reject(Tables.BronzeProducts, "null product_id", runId, r)); continue; }
            if (op == "D") { changes.Add(new DimChange(key!, commitTs, seq, null, IsDelete: true)); continue; }
            if (Parsing.IsBlank(r.GetString("name"))) { rejects.Add(Reject(Tables.BronzeProducts, "null name", runId, r)); continue; }

            decimal? price = null;
            var rawPrice = r.GetString("unit_price");
            if (!Parsing.IsBlank(rawPrice))
            {
                if (!Parsing.TryDecimal(rawPrice, out var p)) { rejects.Add(Reject(Tables.BronzeProducts, "invalid unit_price", runId, r)); continue; }
                if (p < 0) { rejects.Add(Reject(Tables.BronzeProducts, "negative unit_price", runId, r)); continue; }
                price = p;
            }

            var attrs = new Row
            {
                ["name"] = r.GetString("name"),
                ["category"] = r.GetString("category"),
                ["unit_price"] = price,
                ["currency"] = r.GetString("currency"),
                ["active"] = r.GetBool("active")
            };
            changes.Add(new DimChange(key!, commitTs, seq, attrs));
        }

        var versions = Scd2Processor.Build(changes, LakehouseModel.ProductTracked);
        var rows = versions.Select(v =>
        {
            var row = Row.Of(("surrogate_key", v.SurrogateKey), ("product_id", v.BusinessKey),
                ("valid_from", v.ValidFrom), ("valid_to", (object?)v.ValidTo), ("is_current", v.IsCurrent));
            foreach (var col in LakehouseModel.ProductTracked) row[col] = v.Attributes[col];
            return row;
        }).ToList();

        Table(Tables.SilverProducts, LakehouseModel.SilverProducts).Overwrite(rows);
        WriteQuarantine(Tables.BronzeProducts, rejects, runId);
        return new StepResult("silver:products", bronze.Count, rows.Count, rejects.Count);
    }

    // ---- orders ---------------------------------------------------------------------------------

    public StepResult BuildOrders(string runId)
    {
        var bronze = Scan(Tables.BronzeOrders);
        var knownCustomers = DistinctKeys(Tables.BronzeCustomers, "customer_id");
        var rejects = new List<Row>();
        var rows = new List<Row>();

        foreach (var r in LatestByKey(bronze, "order_id"))
        {
            if (r.GetString(Meta.Op) == "D") continue; // tombstoned order — excluded from current state

            var orderId = r.GetString("order_id");
            var customerId = r.GetString("customer_id");
            if (Parsing.IsBlank(customerId)) { rejects.Add(Reject(Tables.BronzeOrders, "null customer_id", runId, r)); continue; }
            if (!knownCustomers.Contains(customerId!)) { rejects.Add(Reject(Tables.BronzeOrders, "orphan customer_id (referential integrity)", runId, r)); continue; }
            if (!Parsing.TryTimestampUtc(r.GetString("order_ts"), out var orderTs)) { rejects.Add(Reject(Tables.BronzeOrders, "invalid order_ts", runId, r)); continue; }

            rows.Add(Row.Of(
                ("order_id", orderId), ("customer_id", customerId),
                ("order_ts", orderTs), ("order_date", orderTs.UtcDateTime.ToString("yyyy-MM-dd")),
                ("channel", r.GetString("channel")), ("currency", r.GetString("currency")),
                ("status", r.GetString("status"))));
        }

        Table(Tables.SilverOrders, LakehouseModel.SilverOrders).Overwrite(rows);
        WriteQuarantine(Tables.BronzeOrders, rejects, runId);
        return new StepResult("silver:orders", bronze.Count, rows.Count, rejects.Count);
    }

    // ---- order lines ----------------------------------------------------------------------------

    public StepResult BuildOrderLines(string runId)
    {
        var bronze = Scan(Tables.BronzeOrderLines);
        var validOrders = DistinctKeys(Tables.SilverOrders, "order_id");
        var knownProducts = DistinctKeys(Tables.BronzeProducts, "product_id");
        var rejects = new List<Row>();
        var rows = new List<Row>();

        foreach (var r in LatestByKey(bronze, "order_line_id"))
        {
            if (r.GetString(Meta.Op) == "D") continue;

            var lineId = r.GetString("order_line_id");
            var orderId = r.GetString("order_id");
            var productId = r.GetString("product_id");

            if (Parsing.IsBlank(productId)) { rejects.Add(Reject(Tables.BronzeOrderLines, "null product_id", runId, r)); continue; }
            if (Parsing.IsBlank(orderId) || !validOrders.Contains(orderId!)) { rejects.Add(Reject(Tables.BronzeOrderLines, "orphan order_id (referential integrity)", runId, r)); continue; }
            if (!knownProducts.Contains(productId!)) { rejects.Add(Reject(Tables.BronzeOrderLines, "orphan product_id (referential integrity)", runId, r)); continue; }

            if (!Parsing.TryLong(r.GetString("quantity"), out var qty)) { rejects.Add(Reject(Tables.BronzeOrderLines, "invalid quantity", runId, r)); continue; }
            if (qty <= 0) { rejects.Add(Reject(Tables.BronzeOrderLines, "non-positive quantity", runId, r)); continue; }
            if (!Parsing.TryDecimal(r.GetString("unit_price"), out var price)) { rejects.Add(Reject(Tables.BronzeOrderLines, "invalid unit_price", runId, r)); continue; }
            if (price < 0) { rejects.Add(Reject(Tables.BronzeOrderLines, "negative unit_price", runId, r)); continue; }

            decimal discount = 0;
            var rawDiscount = r.GetString("discount");
            if (!Parsing.IsBlank(rawDiscount))
            {
                if (!Parsing.TryDecimal(rawDiscount, out discount)) { rejects.Add(Reject(Tables.BronzeOrderLines, "invalid discount", runId, r)); continue; }
                if (discount < 0) { rejects.Add(Reject(Tables.BronzeOrderLines, "negative discount", runId, r)); continue; }
            }

            var currency = r.GetString("currency");
            if (Parsing.IsBlank(currency)) { rejects.Add(Reject(Tables.BronzeOrderLines, "null currency", runId, r)); continue; }

            var gross = qty * price;
            if (discount > gross) { rejects.Add(Reject(Tables.BronzeOrderLines, "discount exceeds gross amount", runId, r)); continue; }
            var net = gross - discount;

            rows.Add(Row.Of(
                ("order_line_id", lineId), ("order_id", orderId), ("product_id", productId),
                ("quantity", qty), ("unit_price", price), ("discount", discount),
                ("currency", currency), ("gross_amount", gross), ("net_amount", net)));
        }

        Table(Tables.SilverOrderLines, LakehouseModel.SilverOrderLines).Overwrite(rows);
        WriteQuarantine(Tables.BronzeOrderLines, rejects, runId);
        return new StepResult("silver:order_lines", bronze.Count, rows.Count, rejects.Count);
    }

    // ---- clickstream ----------------------------------------------------------------------------

    public StepResult BuildClickstream(string runId)
    {
        var bronze = Scan(Tables.BronzeClickstream);
        var rejects = new List<Row>();
        var rows = new List<Row>();

        foreach (var r in LatestByKey(bronze, "event_id"))
        {
            if (r.GetString(Meta.Op) == "D") continue;

            var eventId = r.GetString("event_id");
            var sessionId = r.GetString("session_id");
            if (Parsing.IsBlank(sessionId)) { rejects.Add(Reject(Tables.BronzeClickstream, "null session_id", runId, r)); continue; }
            if (Parsing.IsBlank(r.GetString("event_type"))) { rejects.Add(Reject(Tables.BronzeClickstream, "null event_type", runId, r)); continue; }
            if (!Parsing.TryTimestampUtc(r.GetString("event_ts"), out var eventTs)) { rejects.Add(Reject(Tables.BronzeClickstream, "invalid event_ts", runId, r)); continue; }

            rows.Add(Row.Of(
                ("event_id", eventId), ("session_id", sessionId),
                ("customer_id", r.GetString("customer_id")),
                ("event_ts", eventTs), ("event_date", eventTs.UtcDateTime.ToString("yyyy-MM-dd")),
                ("page", r.GetString("page")), ("event_type", r.GetString("event_type")),
                ("channel", r.GetString("channel"))));
        }

        Table(Tables.SilverClickstream, LakehouseModel.SilverClickstream).Overwrite(rows);
        WriteQuarantine(Tables.BronzeClickstream, rejects, runId);
        return new StepResult("silver:clickstream", bronze.Count, rows.Count, rejects.Count);
    }

    // ---- fx -------------------------------------------------------------------------------------

    public StepResult BuildFx(string runId)
    {
        var bronze = Scan(Tables.BronzeFx);
        var rejects = new List<Row>();
        var rows = new List<Row>();

        foreach (var r in LatestByKey(bronze, "currency", "rate_date"))
        {
            if (r.GetString(Meta.Op) == "D") continue;

            var currency = r.GetString("currency");
            var rateDate = r.GetString("rate_date");
            if (Parsing.IsBlank(currency)) { rejects.Add(Reject(Tables.BronzeFx, "null currency", runId, r)); continue; }
            if (Parsing.IsBlank(rateDate)) { rejects.Add(Reject(Tables.BronzeFx, "null rate_date", runId, r)); continue; }
            if (!Parsing.TryDecimal(r.GetString("rate_to_usd"), out var rate)) { rejects.Add(Reject(Tables.BronzeFx, "invalid rate_to_usd", runId, r)); continue; }
            if (rate <= 0) { rejects.Add(Reject(Tables.BronzeFx, "non-positive rate_to_usd", runId, r)); continue; }

            rows.Add(Row.Of(("currency", currency), ("rate_date", rateDate), ("rate_to_usd", rate)));
        }

        Table(Tables.SilverFx, LakehouseModel.SilverFx).Overwrite(rows);
        WriteQuarantine(Tables.BronzeFx, rejects, runId);
        return new StepResult("silver:fx", bronze.Count, rows.Count, rejects.Count);
    }

    // ---- helpers --------------------------------------------------------------------------------

    private IReadOnlyList<Row> Scan(string table)
    {
        var t = lake.Table(table);
        return t.Exists ? t.Scan() : Array.Empty<Row>();
    }

    /// <summary>Deduplicate a change log to current state: the highest-sequence row per business key wins.</summary>
    private static IEnumerable<Row> LatestByKey(IReadOnlyList<Row> rows, params string[] keyColumns)
        => rows.GroupBy(r => string.Join('\u0001', keyColumns.Select(k => r.GetString(k) ?? "\u0000")), StringComparer.Ordinal)
            .Select(g => g.OrderBy(r => r.GetLong(Meta.Sequence) ?? 0).Last());

    private HashSet<string> DistinctKeys(string table, string column)
        => Scan(table).Select(r => r.GetString(column)).Where(v => !Parsing.IsBlank(v)).Select(v => v!)
            .ToHashSet(StringComparer.Ordinal);

    private ILakeTable Table(string name, Func<TableSchema> schema)
    {
        var t = lake.Table(name);
        if (!t.Exists) t.Create(schema());
        return t;
    }

    private Row Reject(string sourceTable, string reason, string runId, Row raw)
    {
        var seq = raw.GetLong(Meta.Sequence) ?? 0;
        return Row.Of(
            ("quarantine_id", $"{sourceTable}:{seq}:{reason}"),
            ("source_table", sourceTable), ("reason", reason), ("run_id", runId),
            ("rejected_ts", clock.UtcNow), ("payload", Parsing.Payload(raw)));
    }

    private void WriteQuarantine(string sourceTable, IReadOnlyList<Row> rejects, string runId)
    {
        var q = Table(Tables.Quarantine, LakehouseModel.Quarantine);
        q.Delete(r => r.GetString("source_table") == sourceTable); // idempotent: replace this source's rejects
        if (rejects.Count > 0) q.Append(rejects);
    }
}
