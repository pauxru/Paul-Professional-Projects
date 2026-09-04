using LoadRunner.Core.Analysis;
using Xunit;

namespace LoadRunner.UnitTests.Analysis;

public class CapacityBinarySearchTests
{
    [Fact]
    public async Task ConvergesOnKnownServerCapacity()
    {
        // Simulated server: p95 latency = 50ms until rate exceeds 200 rps, then rises linearly.
        var searcher = new CapacityBinarySearch();
        var result = await searcher.SearchAsync(
            minRate: 10,
            maxRate: 1000,
            tolerance: 20,
            p95TargetMs: 100,
            errorRateCeiling: 0.01,
            probe: async (rate, ct) =>
            {
                await Task.Yield();
                var p95 = rate <= 200 ? 50 : 50 + (rate - 200) * 2.0;
                var err = rate > 300 ? Math.Min(0.5, (rate - 300) * 0.005) : 0;
                return new CapacityBinarySearch.ProbeOutcome(p95, err);
            });
        // Truth: capacity ~ 225 rps (where p95 = 100 ms). Allow some binary-search granularity.
        Assert.InRange(result.MaximumSustainedRate, 190, 250);
        Assert.True(result.ProbesRun >= 4 && result.ProbesRun <= 15);
    }

    [Fact]
    public async Task RespectsCancellationBetweenProbes()
    {
        using var cts = new CancellationTokenSource();
        var searcher = new CapacityBinarySearch();
        var task = searcher.SearchAsync(1, 1000, 5, 50, 0.01,
            async (rate, ct) => { await Task.Delay(10, ct); return new CapacityBinarySearch.ProbeOutcome(200, 0); },
            cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }
}
