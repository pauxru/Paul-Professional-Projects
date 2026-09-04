using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.Domain.Services;

public sealed class UnitEconomicsService
{
    public IReadOnlyList<UnitEconomicsPoint> Calculate(IEnumerable<AllocationLine> allocations, IEnumerable<BusinessMetric> metrics)
    {
        var costByTeamDay = allocations
            .Where(x => !string.Equals(x.Team, "unallocated", StringComparison.OrdinalIgnoreCase))
            .GroupBy(x => (x.UsageDate, Team: x.Team.ToLowerInvariant()))
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Amount));

        return metrics
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Team, StringComparer.OrdinalIgnoreCase)
            .Select(metric =>
            {
                var cost = costByTeamDay.GetValueOrDefault((metric.Date, metric.Team.ToLowerInvariant()));
                return new UnitEconomicsPoint(
                    metric.Date,
                    metric.Team,
                    metric.Orders == 0 ? 0m : Round(cost / metric.Orders),
                    metric.ActiveTenants == 0 ? 0m : Round(cost / metric.ActiveTenants),
                    metric.GigabytesProcessed == 0 ? 0m : Round(cost / metric.GigabytesProcessed),
                    Round(cost),
                    metric.Orders,
                    metric.ActiveTenants,
                    metric.GigabytesProcessed);
            })
            .ToList();
    }

    private static decimal Round(decimal value) => decimal.Round(value, 4, MidpointRounding.AwayFromZero);
}

