using Lakehouse.Application.Model;
using Lakehouse.Application.Pipelines;
using Lakehouse.Domain.Cdc;
using Lakehouse.Domain.Data;
using Lakehouse.UnitTests.Support;

namespace Lakehouse.UnitTests;

/// <summary>
/// Bronze ingestion guarantees: full source metadata on every row, append-only immutability, an
/// incremental sequence watermark, and exactly-once-effective ingestion — a replay after a crash between
/// the data commit and the watermark update must not duplicate rows.
/// </summary>
public sealed class BronzeIngestionTests
{
    private static ChangeEvent Customer(long seq, string id, string name, ChangeOp op = ChangeOp.Insert, params (string, object?)[] extra)
    {
        var row = Row.Of(("customer_id", id), ("name", name), ("email", $"{id}@x.io"),
            ("city", "Nairobi"), ("country", "KE"), ("currency", "KES"), ("segment", "retail"),
            ("created_at", "2026-01-01T00:00:00Z"), ("updated_at", "2026-01-01T00:00:00Z"));
        foreach (var (k, v) in extra) row[k] = v;
        return new ChangeEvent("erp", Tables.Customers, op, seq, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMinutes(seq), id, row);
    }

    private BronzeIngestor Ingestor(TempLake lh) => new(lh.Lake, lh.Checkpoints, lh.Clock);

    [Fact]
    public void Ingest_stamps_full_source_metadata()
    {
        using var lh = new TempLake();
        var feed = new[] { Customer(1, "C1", "Ann"), Customer(2, "C2", "Bob") };

        Ingestor(lh).Ingest(feed, Tables.Customers, "run-1");

        var rows = lh.Lake.Table(Tables.BronzeCustomers).Scan();
        Assert.Equal(2, rows.Count);
        var r = rows.First(x => x.GetString("customer_id") == "C1");
        Assert.Equal("I", r.GetString(Meta.Op));
        Assert.Equal(1, r.GetLong(Meta.Sequence));
        Assert.Equal("run-1", r.GetString(Meta.RunId));
        Assert.False(string.IsNullOrEmpty(r.GetString(Meta.Source)));
        Assert.NotNull(r.GetTimestamp(Meta.IngestTs));
        Assert.Equal("Ann", r.GetString("name")); // business column preserved as raw string
    }

    [Fact]
    public void Reingesting_same_feed_does_not_duplicate_rows()
    {
        using var lh = new TempLake();
        var feed = new[] { Customer(1, "C1", "Ann"), Customer(2, "C2", "Bob") };
        var ing = Ingestor(lh);

        ing.Ingest(feed, Tables.Customers, "run-1");
        var second = ing.Ingest(feed, Tables.Customers, "run-2");

        Assert.Equal(0, second.RowsOut); // watermark: nothing new
        Assert.Equal(2, lh.Lake.Table(Tables.BronzeCustomers).Scan().Count);
    }

    [Fact]
    public void Incremental_ingest_only_appends_new_sequences()
    {
        using var lh = new TempLake();
        var ing = Ingestor(lh);
        ing.Ingest(new[] { Customer(1, "C1", "Ann"), Customer(2, "C2", "Bob") }, Tables.Customers, "run-1");

        var bigger = new[] { Customer(1, "C1", "Ann"), Customer(2, "C2", "Bob"), Customer(3, "C3", "Cara") };
        var result = ing.Ingest(bigger, Tables.Customers, "run-2");

        Assert.Equal(1, result.RowsOut);
        Assert.Equal(3, lh.Lake.Table(Tables.BronzeCustomers).Scan().Count);
    }

    [Fact]
    public void Replay_after_watermark_loss_is_idempotent_via_commit_token()
    {
        // Simulate a crash: data was committed but the watermark update was lost. Re-running must not
        // duplicate — the batch's deterministic ingest token is recognised in history and skipped.
        using var lh = new TempLake();
        var feed = new[] { Customer(1, "C1", "Ann"), Customer(2, "C2", "Bob") };
        var ing = Ingestor(lh);

        ing.Ingest(feed, Tables.Customers, "run-1");
        lh.Checkpoints.SetWatermark("bronze:customers", 0); // pretend the watermark never advanced

        var replay = ing.Ingest(feed, Tables.Customers, "run-1");

        Assert.Equal(0, replay.RowsOut); // token match -> no re-ingest
        Assert.Equal(2, lh.Lake.Table(Tables.BronzeCustomers).Scan().Count);
    }

    [Fact]
    public void New_column_appearing_midstream_evolves_bronze_schema()
    {
        using var lh = new TempLake();
        var ing = Ingestor(lh);
        ing.Ingest(new[] { Customer(1, "C1", "Ann") }, Tables.Customers, "run-1");
        Assert.False(lh.Lake.Table(Tables.BronzeCustomers).Schema.Has(LakehouseModel.CustomerEvolvedColumn));

        ing.Ingest(new[] { Customer(2, "C2", "Bob", ChangeOp.Insert, (LakehouseModel.CustomerEvolvedColumn, "gold")) },
            Tables.Customers, "run-2");

        var t = lh.Lake.Table(Tables.BronzeCustomers);
        Assert.True(t.Schema.Has(LakehouseModel.CustomerEvolvedColumn));
        var rows = t.Scan().ToDictionary(r => r.GetString("customer_id")!, r => r);
        Assert.Null(rows["C1"].GetString(LakehouseModel.CustomerEvolvedColumn)); // old row: null
        Assert.Equal("gold", rows["C2"].GetString(LakehouseModel.CustomerEvolvedColumn));
    }

    [Fact]
    public void Backfill_window_is_row_level_idempotent()
    {
        using var lh = new TempLake();
        var ing = Ingestor(lh);
        var feed = new[] { Customer(1, "C1", "Ann"), Customer(2, "C2", "Bob") };
        var from = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = from.AddDays(1);

        ing.IngestWindow(feed, Tables.Customers, "bf-1", from, to);
        var again = ing.IngestWindow(feed, Tables.Customers, "bf-2", from, to);

        Assert.Equal(0, again.RowsOut); // sequences already materialised
        Assert.Equal(2, lh.Lake.Table(Tables.BronzeCustomers).Scan().Count);
    }
}
