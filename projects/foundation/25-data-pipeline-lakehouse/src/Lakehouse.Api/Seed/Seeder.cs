using Lakehouse.Application.Abstractions;
using Lakehouse.Application.Model;
using Lakehouse.Application.Orchestration;
using Lakehouse.Application.Serving;

namespace Lakehouse.Api.Seed;

/// <summary>
/// Idempotent startup seed. On first boot (empty lake) it runs the full DAG once so the API has data to
/// serve; on subsequent boots it detects existing gold tables and skips straight to refreshing the SQLite
/// serving database. Re-running is safe: the medallion transforms are idempotent and the serving DB is a
/// disposable projection of gold.
/// </summary>
public sealed class Seeder(
    LakehousePipeline pipeline,
    DagRunner runner,
    ISqlQueryEngine serving,
    ILakehouse lake,
    IClock clock,
    ILogger<Seeder> logger)
{
    public RunRecord? EnsureSeeded()
    {
        RunRecord? record = null;
        if (!lake.TableExists(Tables.FactOrderLine))
        {
            var runId = $"seed-{clock.UtcNow:yyyyMMddHHmmss}";
            logger.LogInformation("Seeding lakehouse (run {RunId}) — no gold tables found", runId);
            record = runner.Run(pipeline.Build(), RunContext.Full(runId));
            logger.LogInformation("Seed run complete: success={Success} rowsOut={Rows} durationMs={Duration}",
                record.Success, record.TotalRowsOut, record.DurationMs);
        }
        else
        {
            logger.LogInformation("Lakehouse already populated — skipping seed run");
        }

        if (lake.TableExists(Tables.FactOrderLine))
            serving.Rebuild();

        return record;
    }
}
