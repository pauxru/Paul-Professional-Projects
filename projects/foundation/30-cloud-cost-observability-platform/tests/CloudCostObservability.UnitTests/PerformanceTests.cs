using System.Diagnostics;
using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.UnitTests;

public sealed class PerformanceTests
{
    [Fact]
    public void Aggregate_250000HighCardinalityCostRows_CompletesWithinTenSeconds()
    {
        const int rowCount = 250_000;
        var start = new DateOnly(2026, 1, 1);
        var costs = Enumerable.Range(0, rowCount)
            .Select(index => new CostRecord(
                "2026-01",
                $"resource-{index % 400:D3}",
                start.AddDays(index % 31),
                $"meter-{index % 27}",
                $"service-{index % 7}",
                (ResourceCategory)(index % 7),
                1m + index % 10,
                "unit",
                .5m,
                1m + index % 10,
                1m + index % 10))
            .ToList();
        var stopwatch = Stopwatch.StartNew();
        var result = costs
            .GroupBy(cost => (cost.UsageDate, cost.Service, cost.ResourceId))
            .Select(group => new { group.Key, Amount = group.Sum(cost => cost.AmortizedCost), Count = group.Count() })
            .ToList();
        stopwatch.Stop();
        Assert.Equal(rowCount, result.Sum(group => group.Count));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Aggregation took {stopwatch.Elapsed.TotalMilliseconds:0}ms.");
    }
}

