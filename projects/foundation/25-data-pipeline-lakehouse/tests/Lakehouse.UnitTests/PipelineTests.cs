using Lakehouse.Application.Model;
using Lakehouse.Application.Orchestration;
using Lakehouse.Domain.Data;
using Lakehouse.Infrastructure.Sources;
using Lakehouse.UnitTests.Support;

namespace Lakehouse.UnitTests;

/// <summary>
/// End-to-end pipeline behaviour over the synthetic Contoso feed: a full run populates gold, re-running
/// the same window is idempotent (identical results — the incrementality contract), a date-range
/// backfill materialises gold, and a tripped silver quality gate blocks promotion to gold entirely.
/// </summary>
public sealed class PipelineTests
{
    private static GeneratorOptions SmallFeed(double defectRate = 0.03) => new()
    {
        Customers = 60,
        Products = 30,
        Orders = 300,
        Sessions = 200,
        Days = 20,
        DefectRate = defectRate,
        Start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
    };

    private static (int Count, decimal SumUsd) GoldSummary(TempLake lh)
    {
        var f = lh.Lake.Table(Tables.FactOrderLine);
        if (!f.Exists) return (0, 0m);
        var rows = f.Scan();
        return (rows.Count, rows.Sum(r => r.GetDecimal("net_amount_usd") ?? 0m));
    }

    [Fact]
    public void Full_run_populates_gold()
    {
        using var lh = new TempLake();
        var pipeline = lh.Pipeline(new ContosoFeedProvider(SmallFeed()));

        var rec = lh.Runner().Run(pipeline.Build(), RunContext.Full("r1"));

        Assert.True(rec.Success, $"run failed: {rec.Failed} failed, {rec.Blocked} blocked");
        Assert.Equal(TaskState.Succeeded, rec.Tasks.Single(t => t.TaskId == "dq_gold").State);
        Assert.NotEmpty(lh.Lake.Table(Tables.FactOrderLine).Scan());
        Assert.NotEmpty(lh.Lake.Table(Tables.AggDailyRevenue).Scan());
    }

    [Fact]
    public void Rerunning_the_same_window_is_idempotent()
    {
        using var lh = new TempLake();
        var pipeline = lh.Pipeline(new ContosoFeedProvider(SmallFeed()));
        var runner = lh.Runner();

        runner.Run(pipeline.Build(), RunContext.Full("r1"));
        var first = GoldSummary(lh);

        runner.Run(pipeline.Build(), RunContext.Full("r2"));
        var second = GoldSummary(lh);

        Assert.Equal(first.Count, second.Count);
        Assert.Equal(first.SumUsd, second.SumUsd);
        Assert.True(first.Count > 0);
    }

    [Fact]
    public void Backfill_of_a_date_range_materialises_gold()
    {
        using var lh = new TempLake();
        var pipeline = lh.Pipeline(new ContosoFeedProvider(SmallFeed()));

        var records = lh.Runner().Backfill(pipeline.Build(), new[] { "2026-01-01..2026-01-31" },
            w => new RunContext($"bf-{w}", w));

        var rec = Assert.Single(records);
        Assert.True(rec.Success, $"backfill failed: {rec.Failed} failed, {rec.Blocked} blocked");
        Assert.NotEmpty(lh.Lake.Table(Tables.FactOrderLine).Scan());
    }

    [Fact]
    public void Tripped_silver_gate_blocks_promotion_to_gold()
    {
        using var lh = new TempLake();
        var pipeline = lh.Pipeline(new ContosoFeedProvider(SmallFeed(defectRate: 0.25)));

        // Force the quarantine-volume anomaly to trip by pre-seeding an artificially low rolling baseline.
        lh.Checkpoints.Set("dq:quarantine_baseline", "1");

        var rec = lh.Runner().Run(pipeline.Build(), RunContext.Full("r1"));

        Assert.False(rec.Success);
        Assert.Equal(TaskState.Failed, rec.Tasks.Single(t => t.TaskId == "dq_silver").State);
        Assert.Equal(TaskState.Blocked, rec.Tasks.Single(t => t.TaskId == "fact_order_line").State);
        Assert.False(lh.Lake.TableExists(Tables.FactOrderLine)); // gold never built
    }
}
