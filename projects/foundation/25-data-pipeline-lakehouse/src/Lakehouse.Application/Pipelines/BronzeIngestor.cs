using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Model;
using Lakehouse.Domain.Cdc;
using Lakehouse.Domain.Data;
using Lakehouse.Domain.Schemas;

namespace Lakehouse.Application.Pipelines;

/// <summary>
/// Bronze ingestion: append-only, immutable, partitioned by ingest date, with full source metadata on
/// every row. Ingestion is incremental (driven by a per-source sequence watermark), micro-batched, and
/// exactly-once-effective: each batch carries a deterministic idempotency token, so a crash between the
/// data commit and the watermark update cannot double-ingest — the replayed batch is recognised and
/// skipped. Handles a new column appearing mid-stream by evolving the bronze schema before the append.
/// </summary>
public sealed class BronzeIngestor(ILakehouse lake, ICheckpointStore checkpoints, IClock clock)
{
    public int BatchSize { get; init; } = 5000;

    public StepResult Ingest(IEnumerable<ChangeEvent> feed, string entity, string runId)
    {
        var table = lake.Table(Tables.BronzeFor(entity));
        if (!table.Exists) table.Create(BronzeSchema(entity));

        var watermarkKey = $"bronze:{entity}";
        var watermark = checkpoints.GetWatermark(watermarkKey);

        var pending = feed.Where(e => e.Entity == entity && e.Sequence > watermark)
            .OrderBy(e => e.Sequence)
            .ToList();
        if (pending.Count == 0) return new StepResult($"bronze:{entity}", 0, 0, Note: "no new source rows");

        long appended = 0;
        foreach (var batch in Chunk(pending, BatchSize))
        {
            EvolveIfNeeded(table, batch);

            var token = $"{entity}:{batch[0].Sequence}-{batch[^1].Sequence}";
            var alreadyIngested = table.History()
                .Any(s => s.Summary.TryGetValue("ingest_token", out var t) && t == token);

            if (!alreadyIngested)
            {
                var ingestTs = clock.UtcNow;
                var partition = $"ingest_date={ingestTs.UtcDateTime:yyyy-MM-dd}";
                var rows = batch.Select(e => Meta.Stamp(
                    e.After, e.Op.Code(), e.Sequence, e.CommitTs, ingestTs,
                    source: $"{entity}.cdc", sourceOffset: e.Sequence, runId: runId)).ToList();

                table.Append(rows, partition, new Dictionary<string, string>
                {
                    ["ingest_token"] = token,
                    ["entity"] = entity,
                    ["run_id"] = runId,
                    ["hi_seq"] = batch[^1].Sequence.ToString()
                });
                appended += rows.Count;
            }

            checkpoints.SetWatermark(watermarkKey, batch[^1].Sequence);
        }

        return new StepResult($"bronze:{entity}", pending.Count, appended,
            Note: appended == 0 ? "idempotent replay — nothing re-ingested" : null);
    }

    /// <summary>
    /// Backfill a business-time window: (re)ingest source rows whose commit time falls in [from, to),
    /// independent of the incremental watermark. Idempotency is guaranteed at row granularity — any
    /// sequence already present in bronze is skipped — so a backfill that overlaps already-ingested data
    /// cannot create duplicates. The incremental watermark is deliberately left untouched.
    /// </summary>
    public StepResult IngestWindow(IEnumerable<ChangeEvent> feed, string entity, string runId,
        DateTimeOffset fromUtc, DateTimeOffset toUtcExclusive)
    {
        var table = lake.Table(Tables.BronzeFor(entity));
        if (!table.Exists) table.Create(BronzeSchema(entity));

        var existing = new HashSet<long>(table.Scan().Select(r => r.GetLong(Meta.Sequence) ?? -1));
        var pending = feed
            .Where(e => e.Entity == entity && e.CommitTs >= fromUtc && e.CommitTs < toUtcExclusive && !existing.Contains(e.Sequence))
            .OrderBy(e => e.Sequence)
            .ToList();
        if (pending.Count == 0) return new StepResult($"backfill:{entity}", 0, 0, Note: "window already materialised");

        EvolveIfNeeded(table, pending);
        var ingestTs = clock.UtcNow;
        var partition = $"ingest_date={ingestTs.UtcDateTime:yyyy-MM-dd}";
        var rows = pending.Select(e => Meta.Stamp(
            e.After, e.Op.Code(), e.Sequence, e.CommitTs, ingestTs,
            source: $"{entity}.backfill", sourceOffset: e.Sequence, runId: runId)).ToList();

        table.Append(rows, partition, new Dictionary<string, string>
        {
            ["ingest_token"] = $"backfill:{entity}:{fromUtc:yyyy-MM-dd}:{pending[0].Sequence}-{pending[^1].Sequence}",
            ["entity"] = entity,
            ["run_id"] = runId,
            ["backfill_from"] = fromUtc.ToString("O"),
            ["backfill_to"] = toUtcExclusive.ToString("O")
        });

        return new StepResult($"backfill:{entity}", pending.Count, rows.Count, Note: "backfilled window");
    }

    private static void EvolveIfNeeded(ILakeTable table, IReadOnlyList<ChangeEvent> batch)
    {
        var known = new HashSet<string>(table.Schema.Columns.Select(c => c.Name), StringComparer.Ordinal);
        var newColumns = batch
            .SelectMany(e => e.After.Columns)
            .Where(c => !known.Contains(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (newColumns.Count == 0) return;

        var evolved = table.Schema.Evolve(newColumns.Select(c => new ColumnDef(c, ColumnType.String)).ToArray());
        table.EvolveSchema(evolved);
    }

    private static TableSchema BronzeSchema(string entity) => entity switch
    {
        Tables.Customers => LakehouseModel.BronzeCustomers(),
        Tables.Products => LakehouseModel.BronzeProducts(),
        Tables.Orders => LakehouseModel.BronzeOrders(),
        Tables.OrderLines => LakehouseModel.BronzeOrderLines(),
        Tables.Clickstream => LakehouseModel.BronzeClickstream(),
        Tables.Inventory => LakehouseModel.BronzeInventory(),
        Tables.Fx => LakehouseModel.BronzeFx(),
        _ => throw new ArgumentOutOfRangeException(nameof(entity))
    };

    private static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.Skip(i).Take(size).ToList();
    }
}
