using Lakehouse.Application.Model;
using Lakehouse.Application.Pipelines;
using Lakehouse.Domain.Cdc;
using Lakehouse.Domain.Data;
using Lakehouse.UnitTests.Support;

namespace Lakehouse.UnitTests;

/// <summary>
/// Silver transformation over a CDC feed: deduplication by business key + sequence (latest wins, even
/// out of order), typed parsing with a quarantine path that records the rejection reason, and full
/// idempotency — re-running replaces rather than accumulates.
/// </summary>
public sealed class CdcSilverTests
{
    private static readonly DateTimeOffset Base = new(2026, 1, 2, 0, 0, 0, TimeSpan.Zero);

    private static ChangeEvent Customer(long seq, string id)
        => new("erp", Tables.Customers, ChangeOp.Insert, seq, Base.AddMinutes(seq), id,
            Row.Of(("customer_id", id), ("name", "N" + id), ("email", id + "@x.io"),
                ("city", "Nairobi"), ("country", "KE"), ("currency", "KES"), ("segment", "retail"),
                ("created_at", "2026-01-01T00:00:00Z"), ("updated_at", "2026-01-01T00:00:00Z")));

    private static ChangeEvent Order(long seq, string id, string? cust, string status, string ts = "2026-01-02T10:00:00Z", ChangeOp op = ChangeOp.Insert)
        => new("erp", Tables.Orders, op, seq, Base.AddMinutes(seq), id,
            Row.Of(("order_id", id), ("customer_id", cust), ("order_ts", ts),
                ("channel", "web"), ("currency", "KES"), ("status", status), ("updated_at", ts)));

    private static (TempLake lh, SilverBuilder silver) Setup(IEnumerable<ChangeEvent> customers, IEnumerable<ChangeEvent> orders)
    {
        var lh = new TempLake();
        var bronze = new BronzeIngestor(lh.Lake, lh.Checkpoints, lh.Clock);
        bronze.Ingest(customers, Tables.Customers, "r1");
        bronze.Ingest(orders, Tables.Orders, "r1");
        var silver = new SilverBuilder(lh.Lake, lh.Clock);
        silver.BuildCustomers("r1");
        return (lh, silver);
    }

    [Fact]
    public void Deduplicates_by_business_key_latest_sequence_wins()
    {
        var (lh, silver) = Setup(new[] { Customer(1, "C1") },
            new[] { Order(10, "O1", "C1", "new"), Order(11, "O1", "C1", "shipped") });
        using (lh)
        {
            silver.BuildOrders("r1");
            var rows = lh.Lake.Table(Tables.SilverOrders).Scan();
            Assert.Single(rows);
            Assert.Equal("shipped", rows[0].GetString("status"));
        }
    }

    [Fact]
    public void Out_of_order_cdc_still_takes_highest_sequence()
    {
        var (lh, silver) = Setup(new[] { Customer(1, "C1") },
            new[] { Order(11, "O1", "C1", "shipped"), Order(10, "O1", "C1", "new") }); // newest first
        using (lh)
        {
            silver.BuildOrders("r1");
            var rows = lh.Lake.Table(Tables.SilverOrders).Scan();
            Assert.Single(rows);
            Assert.Equal("shipped", rows[0].GetString("status"));
        }
    }

    [Fact]
    public void Bad_rows_are_quarantined_with_reasons()
    {
        var (lh, silver) = Setup(new[] { Customer(1, "C1") }, new[]
        {
            Order(10, "O1", "C1", "ok"),                         // valid
            Order(11, "O2", null, "ok"),                         // null customer_id
            Order(12, "O3", "C999", "ok"),                       // orphan FK
            Order(13, "O4", "C1", "ok", ts: "not-a-date")        // bad timestamp
        });
        using (lh)
        {
            silver.BuildOrders("r1");
            var q = lh.Lake.Table(Tables.Quarantine).Scan();
            var reasons = q.Select(r => r.GetString("reason")).ToList();
            Assert.Contains(reasons, r => r!.Contains("null customer_id"));
            Assert.Contains(reasons, r => r!.Contains("orphan customer_id"));
            Assert.Contains(reasons, r => r!.Contains("invalid order_ts"));
            Assert.Single(lh.Lake.Table(Tables.SilverOrders).Scan()); // only the valid one promoted
        }
    }

    [Fact]
    public void Rerun_is_idempotent_and_quarantine_is_not_accumulated()
    {
        var (lh, silver) = Setup(new[] { Customer(1, "C1") }, new[]
        {
            Order(10, "O1", "C1", "ok"),
            Order(12, "O3", "C999", "ok") // one persistent defect
        });
        using (lh)
        {
            silver.BuildOrders("r1");
            var orders1 = lh.Lake.Table(Tables.SilverOrders).Scan().Count;
            var quar1 = lh.Lake.Table(Tables.Quarantine).Scan().Count;

            silver.BuildOrders("r2"); // re-run same window
            var orders2 = lh.Lake.Table(Tables.SilverOrders).Scan().Count;
            var quar2 = lh.Lake.Table(Tables.Quarantine).Scan().Count;

            Assert.Equal(orders1, orders2);
            Assert.Equal(quar1, quar2); // replaced, not doubled
        }
    }
}
