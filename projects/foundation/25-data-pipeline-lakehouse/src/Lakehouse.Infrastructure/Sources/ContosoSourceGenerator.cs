using System.Globalization;
using Lakehouse.Application.Model;
using Lakehouse.Domain.Cdc;
using Lakehouse.Domain.Data;

namespace Lakehouse.Infrastructure.Sources;

/// <summary>Knobs for the synthetic OLTP source. All generation is deterministic given <see cref="Seed"/>.</summary>
public sealed record GeneratorOptions
{
    public int Seed { get; init; } = 42;
    public int Customers { get; init; } = 200;
    public int Products { get; init; } = 80;
    public int Orders { get; init; } = 3000;
    public int Sessions { get; init; } = 2000;
    public int Days { get; init; } = 90;
    public double DefectRate { get; init; } = 0.03;
    public double LateArrivalRate { get; init; } = 0.06;
    public bool SchemaEvolution { get; init; } = true;
    public DateTimeOffset Start { get; init; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
}

/// <summary>The generated change feed plus a count breakdown for observability/tests.</summary>
public sealed record SourceFeed(IReadOnlyList<ChangeEvent> Events)
{
    public IEnumerable<ChangeEvent> For(string entity) => Events.Where(e => e.Entity == entity);
    public int Count => Events.Count;
}

/// <summary>
/// A synthetic Contoso Retail OLTP system emitting a CDC change feed (op/sequence/commitTs) for
/// customers, products, orders and order-lines, plus append-only clickstream, inventory and FX streams.
/// It deliberately injects realistic messiness: late-arriving and out-of-order updates, deletes
/// (tombstones), a new column appearing mid-stream (schema evolution) and data-quality defects
/// (nulls in required fields, negative amounts, invalid FKs, duplicate ids, bad dates, unknown currencies).
/// </summary>
public sealed class ContosoSourceGenerator
{
    private static readonly string[] FirstNames = { "Amina", "Brian", "Cynthia", "David", "Esther", "Felix", "Grace", "Hassan", "Irene", "John", "Kevin", "Lydia", "Mercy", "Noah", "Otieno", "Peter", "Quincy", "Ruth", "Samuel", "Tabitha" };
    private static readonly string[] LastNames = { "Achieng", "Barasa", "Chebet", "Diallo", "Egan", "Farah", "Gitau", "Hlophe", "Ismail", "Juma", "Kamau", "Lopez", "Mwangi", "Njoroge", "Omondi", "Patel", "Quaye", "Rotich", "Smith", "Torres" };
    private static readonly (string City, string Country, string Currency)[] Geo =
    {
        ("Nairobi", "KE", "KES"), ("Mombasa", "KE", "KES"), ("Kisumu", "KE", "KES"),
        ("London", "UK", "GBP"), ("New York", "US", "USD"), ("Berlin", "DE", "EUR")
    };
    private static readonly string[] Segments = { "Consumer", "SME", "Enterprise" };
    private static readonly string[] LoyaltyTiers = { "Bronze", "Silver", "Gold", "Platinum" };
    private static readonly string[] Categories = { "Electronics", "Apparel", "Home", "Grocery", "Beauty", "Sports" };
    private static readonly string[] Channels = { "web", "mobile", "store", "partner" };
    private static readonly string[] Pages = { "/home", "/search", "/product", "/cart", "/checkout", "/confirmation" };

    private readonly GeneratorOptions _opt;
    private readonly Random _rng;

    public ContosoSourceGenerator(GeneratorOptions? options = null)
    {
        _opt = options ?? new GeneratorOptions();
        _rng = new Random(_opt.Seed);
    }

    public SourceFeed Generate()
    {
        var draft = new List<(string Entity, ChangeOp Op, DateTimeOffset CommitTs, string Key, Row After)>();

        var customerIds = GenerateCustomers(draft);
        var productIds = GenerateProducts(draft);
        GenerateFx(draft);
        GenerateOrders(draft, customerIds, productIds);
        GenerateClickstream(draft, customerIds);
        GenerateInventory(draft, productIds);

        // Order the log roughly by commit time, then inject late arrivals so sequence != commitTs order.
        var ordered = draft.OrderBy(d => d.CommitTs).ToList();
        InjectLateArrivals(ordered);

        long seq = 0;
        var events = new List<ChangeEvent>(ordered.Count);
        foreach (var d in ordered)
            events.Add(new ChangeEvent("contoso-oltp", d.Entity, d.Op, ++seq, d.CommitTs, d.Key, d.After));

        return new SourceFeed(events);
    }

    // ---- customers (SCD2 source, schema evolution, tombstones) ----------------------------------

    private List<string> GenerateCustomers(List<(string, ChangeOp, DateTimeOffset, string, Row)> draft)
    {
        var ids = new List<string>();
        var evolveAfter = _opt.Customers / 2; // loyalty_tier appears once we pass halfway
        for (var i = 1; i <= _opt.Customers; i++)
        {
            var id = $"CUST-{i:D5}";
            ids.Add(id);
            var (city, country, currency) = Geo[_rng.Next(Geo.Length)];
            var created = _opt.Start.AddDays(_rng.Next(_opt.Days)).AddMinutes(_rng.Next(1440));
            var hasLoyalty = _opt.SchemaEvolution && i > evolveAfter;

            var attrs = new Row
            {
                ["customer_id"] = id,
                ["name"] = $"{Pick(FirstNames)} {Pick(LastNames)}",
                ["email"] = $"user{i}@example.com",
                ["city"] = city,
                ["country"] = country,
                ["currency"] = currency,
                ["segment"] = Pick(Segments),
                ["created_at"] = Iso(created),
                ["updated_at"] = Iso(created)
            };
            if (hasLoyalty) attrs["loyalty_tier"] = Pick(LoyaltyTiers);

            MaybeDefect(attrs, Tables.Customers);
            draft.Add((Tables.Customers, ChangeOp.Insert, created, id, attrs));

            // Slowly-changing updates (segment/city/loyalty), some intentionally out of order.
            var updates = _rng.Next(0, 3);
            var lastTs = created;
            for (var u = 0; u < updates; u++)
            {
                lastTs = lastTs.AddDays(_rng.Next(1, 20));
                var changed = attrs.Clone();
                changed["segment"] = Pick(Segments);
                changed["city"] = Geo[_rng.Next(Geo.Length)].City;
                if (hasLoyalty) changed["loyalty_tier"] = Pick(LoyaltyTiers);
                var ts = _rng.NextDouble() < 0.25 ? created.AddHours(_rng.Next(1, 12)) : lastTs; // out-of-order
                changed["updated_at"] = Iso(ts);
                draft.Add((Tables.Customers, ChangeOp.Update, ts, id, changed));
            }

            // A few customers churn (tombstone) then re-activate.
            if (_rng.NextDouble() < 0.05)
            {
                var delTs = lastTs.AddDays(_rng.Next(1, 10));
                draft.Add((Tables.Customers, ChangeOp.Delete, delTs, id, attrs.Clone()));
                if (_rng.NextDouble() < 0.5)
                {
                    var reTs = delTs.AddDays(_rng.Next(1, 15));
                    var reborn = attrs.Clone();
                    reborn["segment"] = Pick(Segments);
                    reborn["updated_at"] = Iso(reTs);
                    draft.Add((Tables.Customers, ChangeOp.Insert, reTs, id, reborn));
                }
            }
        }
        return ids;
    }

    // ---- products (SCD2 source) -----------------------------------------------------------------

    private List<string> GenerateProducts(List<(string, ChangeOp, DateTimeOffset, string, Row)> draft)
    {
        var ids = new List<string>();
        for (var i = 1; i <= _opt.Products; i++)
        {
            var id = $"PROD-{i:D4}";
            ids.Add(id);
            var created = _opt.Start.AddDays(_rng.Next(Math.Min(10, _opt.Days)));
            var price = Math.Round(5 + _rng.NextDouble() * 495, 2);
            var attrs = new Row
            {
                ["product_id"] = id,
                ["name"] = $"{Pick(Categories)} Item {i}",
                ["category"] = Pick(Categories),
                ["unit_price"] = price.ToString(CultureInfo.InvariantCulture),
                ["currency"] = "USD",
                ["active"] = "true",
                ["created_at"] = Iso(created),
                ["updated_at"] = Iso(created)
            };
            MaybeDefect(attrs, Tables.Products);
            draft.Add((Tables.Products, ChangeOp.Insert, created, id, attrs));

            // price changes over time (drives product SCD2)
            var changes = _rng.Next(0, 3);
            var ts = created;
            for (var c = 0; c < changes; c++)
            {
                ts = ts.AddDays(_rng.Next(5, 30));
                var changed = attrs.Clone();
                var newPrice = Math.Round(price * (0.8 + _rng.NextDouble() * 0.6), 2);
                changed["unit_price"] = newPrice.ToString(CultureInfo.InvariantCulture);
                if (_rng.NextDouble() < 0.1) changed["active"] = "false";
                changed["updated_at"] = Iso(ts);
                draft.Add((Tables.Products, ChangeOp.Update, ts, id, changed));
            }
        }
        return ids;
    }

    private void GenerateFx(List<(string, ChangeOp, DateTimeOffset, string, Row)> draft)
    {
        // rate_to_usd: how many USD one unit of the currency is worth.
        var rates = new Dictionary<string, double> { ["USD"] = 1.0, ["KES"] = 0.0077, ["GBP"] = 1.27, ["EUR"] = 1.08 };
        for (var d = 0; d < _opt.Days; d++)
        {
            var date = _opt.Start.AddDays(d);
            foreach (var (ccy, baseRate) in rates)
            {
                var jitter = ccy == "USD" ? 1.0 : 1 + (_rng.NextDouble() - 0.5) * 0.02;
                var row = new Row
                {
                    ["currency"] = ccy,
                    ["rate_date"] = date.UtcDateTime.ToString("yyyy-MM-dd"),
                    ["rate_to_usd"] = Math.Round(baseRate * jitter, 6).ToString(CultureInfo.InvariantCulture)
                };
                draft.Add((Tables.Fx, ChangeOp.Insert, date, $"{ccy}|{date:yyyy-MM-dd}", row));
            }
        }
    }

    // ---- orders + lines (facts with defects, status updates, cancellations) ---------------------

    private void GenerateOrders(List<(string, ChangeOp, DateTimeOffset, string, Row)> draft, List<string> customers, List<string> products)
    {
        for (var i = 1; i <= _opt.Orders; i++)
        {
            var orderId = $"ORD-{i:D6}";
            var customer = Pick(customers);
            var ts = _opt.Start.AddDays(_rng.Next(_opt.Days)).AddMinutes(_rng.Next(1440));
            var (city, country, currency) = Geo[customers.IndexOf(customer) >= 0 ? _rng.Next(Geo.Length) : 0];
            var channel = Pick(Channels);

            var order = new Row
            {
                ["order_id"] = orderId,
                ["customer_id"] = customer,
                ["order_ts"] = Iso(ts),
                ["channel"] = channel,
                ["currency"] = currency,
                ["status"] = "placed",
                ["updated_at"] = Iso(ts)
            };
            MaybeDefect(order, Tables.Orders);
            draft.Add((Tables.Orders, ChangeOp.Insert, ts, orderId, order));

            // status lifecycle
            if (_rng.NextDouble() < 0.8)
            {
                var shipTs = ts.AddDays(_rng.Next(1, 4));
                var shipped = order.Clone();
                shipped["status"] = "shipped";
                shipped["updated_at"] = Iso(shipTs);
                draft.Add((Tables.Orders, ChangeOp.Update, shipTs, orderId, shipped));
            }
            else if (_rng.NextDouble() < 0.5)
            {
                var cancelTs = ts.AddHours(_rng.Next(1, 48));
                draft.Add((Tables.Orders, ChangeOp.Delete, cancelTs, orderId, order.Clone()));
            }

            // order lines
            var lineCount = 1 + _rng.Next(4);
            for (var l = 1; l <= lineCount; l++)
            {
                var lineId = $"{orderId}-L{l}";
                var product = Pick(products);
                var qty = 1 + _rng.Next(5);
                var price = Math.Round(5 + _rng.NextDouble() * 300, 2);
                var discount = _rng.NextDouble() < 0.3 ? Math.Round(_rng.NextDouble() * 20, 2) : 0;
                var line = new Row
                {
                    ["order_line_id"] = lineId,
                    ["order_id"] = orderId,
                    ["product_id"] = product,
                    ["quantity"] = qty.ToString(CultureInfo.InvariantCulture),
                    ["unit_price"] = price.ToString(CultureInfo.InvariantCulture),
                    ["discount"] = discount.ToString(CultureInfo.InvariantCulture),
                    ["currency"] = currency
                };
                MaybeDefect(line, Tables.OrderLines);
                draft.Add((Tables.OrderLines, ChangeOp.Insert, ts, lineId, line));

                // occasional duplicate id (same key emitted twice) → silver must dedup
                if (_rng.NextDouble() < 0.02)
                    draft.Add((Tables.OrderLines, ChangeOp.Insert, ts.AddSeconds(1), lineId, line.Clone()));
            }
        }
    }

    private void GenerateClickstream(List<(string, ChangeOp, DateTimeOffset, string, Row)> draft, List<string> customers)
    {
        long ev = 0;
        for (var s = 1; s <= _opt.Sessions; s++)
        {
            var sessionId = $"SESS-{s:D6}";
            var channel = Pick(Channels);
            var known = _rng.NextDouble() < 0.6;
            var customer = known ? Pick(customers) : null;
            var start = _opt.Start.AddDays(_rng.Next(_opt.Days)).AddMinutes(_rng.Next(1440));

            // funnel depth: most sessions bounce, fewer convert
            var depth = WeightedFunnelDepth();
            for (var p = 0; p < depth; p++)
            {
                var ts = start.AddSeconds(p * (10 + _rng.Next(120)));
                var row = new Row
                {
                    ["event_id"] = $"EVT-{++ev:D8}",
                    ["session_id"] = sessionId,
                    ["customer_id"] = customer,
                    ["event_ts"] = Iso(ts),
                    ["page"] = Pages[Math.Min(p, Pages.Length - 1)],
                    ["event_type"] = p switch { 0 => "view", 3 => "add_to_cart", 4 => "checkout", 5 => "purchase", _ => "view" },
                    ["channel"] = channel
                };
                draft.Add((Tables.Clickstream, ChangeOp.Insert, ts, row.GetString("event_id")!, row));
            }
        }
    }

    private void GenerateInventory(List<(string, ChangeOp, DateTimeOffset, string, Row)> draft, List<string> products)
    {
        long mv = 0;
        foreach (var product in products)
        {
            // initial restock
            draft.Add((Tables.Inventory, ChangeOp.Insert, _opt.Start, $"MV-{++mv:D8}", new Row
            {
                ["movement_id"] = $"MV-{mv:D8}",
                ["product_id"] = product,
                ["event_ts"] = Iso(_opt.Start),
                ["delta_qty"] = (100 + _rng.Next(400)).ToString(CultureInfo.InvariantCulture),
                ["reason"] = "restock"
            }));
            var moves = _rng.Next(3, 12);
            for (var m = 0; m < moves; m++)
            {
                var ts = _opt.Start.AddDays(_rng.Next(_opt.Days));
                var delta = -(1 + _rng.Next(20));
                draft.Add((Tables.Inventory, ChangeOp.Insert, ts, $"MV-{++mv:D8}", new Row
                {
                    ["movement_id"] = $"MV-{mv:D8}",
                    ["product_id"] = product,
                    ["event_ts"] = Iso(ts),
                    ["delta_qty"] = delta.ToString(CultureInfo.InvariantCulture),
                    ["reason"] = "sale"
                }));
            }
        }
    }

    // ---- helpers --------------------------------------------------------------------------------

    private int WeightedFunnelDepth()
    {
        var r = _rng.NextDouble();
        return r switch
        {
            < 0.35 => 1,
            < 0.60 => 2,
            < 0.78 => 3,
            < 0.90 => 4,
            < 0.97 => 5,
            _ => 6
        };
    }

    private void InjectLateArrivals(List<(string Entity, ChangeOp Op, DateTimeOffset CommitTs, string Key, Row After)> ordered)
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            if (_rng.NextDouble() < _opt.LateArrivalRate)
            {
                var target = Math.Min(ordered.Count - 1, i + _rng.Next(1, 30));
                var item = ordered[i];
                ordered.RemoveAt(i);
                ordered.Insert(target, item);
            }
        }
    }

    private void MaybeDefect(Row row, string entity)
    {
        if (_rng.NextDouble() >= _opt.DefectRate) return;
        switch (entity)
        {
            case Tables.Customers:
                if (_rng.NextDouble() < 0.5) row["name"] = null; else row["email"] = null;
                break;
            case Tables.Products:
                row["unit_price"] = (-Math.Round(_rng.NextDouble() * 50, 2)).ToString(CultureInfo.InvariantCulture);
                break;
            case Tables.Orders:
                var pick = _rng.Next(3);
                if (pick == 0) row["customer_id"] = "CUST-99999";        // invalid FK
                else if (pick == 1) row["order_ts"] = "not-a-date";      // bad date
                else row["currency"] = "XXX";                            // unknown currency
                break;
            case Tables.OrderLines:
                var p = _rng.Next(3);
                if (p == 0) row["quantity"] = "-" + (1 + _rng.Next(5)); // negative qty
                else if (p == 1) row["unit_price"] = "abc";             // non-numeric
                else row["product_id"] = null;                          // null FK
                break;
        }
    }

    private string Pick(IReadOnlyList<string> pool) => pool[_rng.Next(pool.Count)];
    private static string Iso(DateTimeOffset ts) => ts.ToString("O", CultureInfo.InvariantCulture);
}
