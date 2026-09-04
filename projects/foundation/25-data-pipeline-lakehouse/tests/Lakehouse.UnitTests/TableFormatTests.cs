using Lakehouse.Domain.Data;
using Lakehouse.Domain.Schemas;
using Lakehouse.UnitTests.Support;

namespace Lakehouse.UnitTests;

/// <summary>
/// The custom Delta-like table format: atomic commits, snapshot isolation, time travel, MERGE upsert,
/// delete-by-predicate and additive schema evolution. These are the foundational guarantees every layer
/// above relies on.
/// </summary>
public sealed class TableFormatTests
{
    private static TableSchema Kv() => new(1, new[]
    {
        new ColumnDef("id", ColumnType.String, false),
        new ColumnDef("n", ColumnType.Long, false)
    }, new[] { "id" });

    private static Row Kv(string id, long n) => Row.Of(("id", id), ("n", n));

    [Fact]
    public void Append_then_scan_returns_all_rows()
    {
        using var lh = new TempLake();
        var t = lh.Lake.Table("t");
        t.Create(Kv());
        t.Append(new[] { Kv("a", 1), Kv("b", 2) });

        var rows = t.Scan();
        Assert.Equal(2, rows.Count);
        Assert.Equal(new[] { "a", "b" }, rows.Select(r => r.GetString("id")).OrderBy(x => x));
    }

    [Fact]
    public void Snapshot_isolation_scan_by_id_is_stable_across_later_commits()
    {
        using var lh = new TempLake();
        var t = lh.Lake.Table("t");
        t.Create(Kv());
        t.Append(new[] { Kv("a", 1) });
        var s1 = t.CurrentSnapshotId;

        t.Append(new[] { Kv("b", 2) });

        // A reader pinned to s1 never sees rows committed after it.
        Assert.Single(t.Scan(s1));
        Assert.Equal(2, t.Scan().Count);
        Assert.True(t.CurrentSnapshotId > s1);
    }

    [Fact]
    public void Time_travel_scan_as_of_timestamp_returns_historical_state()
    {
        using var lh = new TempLake();
        var t = lh.Lake.Table("t");
        t.Create(Kv());

        lh.Clock.Advance(TimeSpan.FromMinutes(1));
        t.Append(new[] { Kv("a", 1) });
        var afterFirst = lh.Clock.UtcNow;

        lh.Clock.Advance(TimeSpan.FromMinutes(1));
        t.Append(new[] { Kv("b", 2) });

        Assert.Single(t.ScanAsOf(afterFirst));
        Assert.Equal(2, t.ScanAsOf(lh.Clock.UtcNow).Count);
    }

    [Fact]
    public void Merge_updates_matched_and_inserts_unmatched()
    {
        using var lh = new TempLake();
        var t = lh.Lake.Table("t");
        t.Create(Kv());
        t.Append(new[] { Kv("a", 1), Kv("b", 2) });

        t.Merge(new[] { Kv("a", 10), Kv("c", 3) }, new[] { "id" });

        var byId = t.Scan().ToDictionary(r => r.GetString("id")!, r => r.GetLong("n"));
        Assert.Equal(3, byId.Count);
        Assert.Equal(10, byId["a"]); // matched -> replaced
        Assert.Equal(2, byId["b"]);  // untouched
        Assert.Equal(3, byId["c"]);  // unmatched -> inserted
    }

    [Fact]
    public void Delete_by_predicate_removes_only_matching_rows()
    {
        using var lh = new TempLake();
        var t = lh.Lake.Table("t");
        t.Create(Kv());
        t.Append(new[] { Kv("a", 1), Kv("b", 2), Kv("c", 3) });

        t.Delete(r => (r.GetLong("n") ?? 0) >= 2);

        var rows = t.Scan();
        Assert.Single(rows);
        Assert.Equal("a", rows[0].GetString("id"));
    }

    [Fact]
    public void Schema_evolution_reads_old_and_new_files_together()
    {
        using var lh = new TempLake();
        var t = lh.Lake.Table("t");
        t.Create(Kv());
        t.Append(new[] { Kv("a", 1) });

        var v2 = t.Schema.Evolve(new ColumnDef("note", ColumnType.String, true));
        t.EvolveSchema(v2);
        t.Append(new[] { Row.Of(("id", "b"), ("n", 2L), ("note", "hello")) });

        var rows = t.Scan().ToDictionary(r => r.GetString("id")!, r => r);
        Assert.Equal(2, t.Schema.Version);
        Assert.Null(rows["a"].GetString("note"));    // old file: new column reads as null
        Assert.Equal("hello", rows["b"].GetString("note"));
    }

    [Fact]
    public void History_grows_by_one_per_commit()
    {
        using var lh = new TempLake();
        var t = lh.Lake.Table("t");
        t.Create(Kv());
        t.Append(new[] { Kv("a", 1) });
        t.Append(new[] { Kv("b", 2) });

        Assert.Equal(3, t.History().Count); // create + 2 appends
    }
}
