using System.Diagnostics;
using Lakehouse.Application.Model;
using Lakehouse.Application.Orchestration;
using Lakehouse.Infrastructure.Sources;
using Lakehouse.UnitTests.Support;

namespace Lakehouse.UnitTests;

/// <summary>
/// Throughput: the platform must ingest and transform a realistically large synthetic batch
/// (&gt;= 100,000 source rows) through bronze → silver → gold within a bounded time. This guards against
/// accidental O(n^2) regressions in the transforms and proves the engine handles non-trivial volume.
/// </summary>
public sealed class ThroughputTests
{
    [Fact]
    public void Processes_at_least_100k_rows_within_time_budget()
    {
        using var lh = new TempLake();
        var options = new GeneratorOptions
        {
            Customers = 2_000,
            Products = 500,
            Orders = 25_000,
            Sessions = 15_000,
            Days = 90,
            Start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)
        };
        var pipeline = lh.Pipeline(new ContosoFeedProvider(options));

        var sw = Stopwatch.StartNew();
        var rec = lh.Runner().Run(pipeline.Build(), RunContext.Full("throughput"));
        sw.Stop();

        Assert.True(rec.Success, $"run failed: {rec.Failed} failed, {rec.Blocked} blocked");

        // Total rows landed in the immutable bronze layer (the honest measure of ingested volume).
        long bronzeRows = LakehouseModel.SourceEntities
            .Select(e => $"bronze_{e}")
            .Where(lh.Lake.TableExists)
            .Sum(t => (long)lh.Lake.Table(t).Scan().Count);

        Assert.True(bronzeRows >= 100_000, $"expected >= 100,000 bronze rows, got {bronzeRows:N0}");
        Assert.NotEmpty(lh.Lake.Table(Tables.FactOrderLine).Scan());
        Assert.True(sw.Elapsed.TotalSeconds < 120, $"throughput run took {sw.Elapsed.TotalSeconds:F1}s (budget 120s)");
    }
}
