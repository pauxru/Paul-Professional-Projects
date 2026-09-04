using CloudCostObservability.Domain.Models;

namespace CloudCostObservability.Domain.Services;

public sealed record OptimizationSignal(
    CloudResource Resource,
    decimal MonthlyCost,
    decimal AverageCpuPercent,
    decimal AverageMemoryPercent,
    int ConsecutiveUnderutilizedDays,
    decimal MonthlyRunningHours,
    int MonthlyAccessCount,
    decimal StableBaselineCoveragePercent,
    bool IsAttached = true,
    bool IsUnusedPublicIp = false,
    bool IsEmptyLoadBalancer = false,
    bool IsStaleSnapshot = false);

public sealed class RecommendationEngine
{
    public IReadOnlyList<Recommendation> Generate(IEnumerable<OptimizationSignal> signals)
    {
        var recommendations = new List<Recommendation>();
        foreach (var signal in signals)
        {
            var resource = signal.Resource;
            var tags = resource.Tags;
            var isProduction = tags.TryGetValue("environment", out var environment) && string.Equals(environment, "production", StringComparison.OrdinalIgnoreCase);
            var missingRequiredTag = new[] { "owner", "team", "environment", "cost-centre", "application" }
                .Any(tag => !tags.TryGetValue(tag, out var value) || string.IsNullOrWhiteSpace(value));

            if (resource.Category == ResourceCategory.Compute && signal.AverageCpuPercent < 10m && signal.AverageMemoryPercent < 20m && signal.ConsecutiveUnderutilizedDays >= 14)
                recommendations.Add(Create(RecommendationType.IdleCompute, resource, Half(signal.MonthlyCost), ConfidenceLevel.High,
                    $"CPU {signal.AverageCpuPercent:0.#}% and memory {signal.AverageMemoryPercent:0.#}% for {signal.ConsecutiveUnderutilizedDays} consecutive days.",
                    "Confirm workload ownership, stop or deallocate the instance, then observe the next billing period."));

            if (!signal.IsAttached || signal.IsUnusedPublicIp || signal.IsEmptyLoadBalancer || signal.IsStaleSnapshot)
            {
                var orphanKind = !signal.IsAttached ? "unattached" : signal.IsUnusedPublicIp ? "unused public IP" : signal.IsEmptyLoadBalancer ? "empty load balancer" : "stale snapshot";
                recommendations.Add(Create(RecommendationType.OrphanedResource, resource, signal.MonthlyCost, ConfidenceLevel.High,
                    $"{orphanKind} resource has no observed consumer.",
                    "Validate references, remove the orphan, and verify that post-change cost falls by the projected amount."));
            }

            if (resource.Category == ResourceCategory.Compute && signal.AverageCpuPercent < 35m && signal.AverageMemoryPercent < 50m && signal.ConsecutiveUnderutilizedDays >= 7)
            {
                var rightSizeSavings = Round(signal.MonthlyCost * .35m);
                recommendations.Add(Create(RecommendationType.OversizedSku, resource, rightSizeSavings, ConfidenceLevel.Medium,
                    $"CPU {signal.AverageCpuPercent:0.#}% and memory {signal.AverageMemoryPercent:0.#}% leave capacity above 30% headroom.",
                    "Benchmark a one-size-smaller SKU with a 30% utilisation headroom, change during an approved window, and monitor saturation."));
            }

            if (!isProduction && resource.Category is ResourceCategory.Compute or ResourceCategory.Database && signal.MonthlyRunningHours > 220m)
            {
                var savedHours = Math.Max(0m, signal.MonthlyRunningHours - 220m);
                var savings = signal.MonthlyRunningHours == 0 ? 0m : Round(signal.MonthlyCost * savedHours / signal.MonthlyRunningHours);
                recommendations.Add(Create(RecommendationType.NonProductionSchedule, resource, savings, ConfidenceLevel.High,
                    $"{savedHours:0.#} hours/month can be eliminated outside approved business hours.",
                    "Configure an owner-approved auto-shutdown schedule, exempt release windows, and verify billing after one full month."));
            }

            if (resource.Category == ResourceCategory.Storage && signal.MonthlyAccessCount <= 2)
                recommendations.Add(Create(RecommendationType.StorageTiering, resource, Round(signal.MonthlyCost * .40m), ConfidenceLevel.Medium,
                    $"{signal.MonthlyAccessCount} observed accesses/month indicates a cool/archive access pattern.",
                    "Apply lifecycle tiering to cool storage, validate retrieval requirements, then observe the storage meter."));

            if (resource.Category == ResourceCategory.Compute && signal.StableBaselineCoveragePercent >= 70m)
            {
                var discount = .28m;
                recommendations.Add(Create(RecommendationType.CommitmentPurchase, resource, Round(signal.MonthlyCost * discount), ConfidenceLevel.Medium,
                    $"{signal.StableBaselineCoveragePercent:0.#}% of usage is stable; a 28% commitment discount breaks even after 12 months.",
                    "Validate baseline demand and term flexibility, purchase a commitment for the stable floor only, and track utilisation monthly."));
            }

            if (missingRequiredTag)
                recommendations.Add(Create(RecommendationType.UntaggedResource, resource, 0m, ConfidenceLevel.High,
                    "One or more required allocation tags is missing, preventing direct ownership.",
                    "Assign owner, team, environment, cost-centre and application; re-run allocation and close the work item."));
        }
        return recommendations;
    }

    private static Recommendation Create(RecommendationType type, CloudResource resource, decimal savings, ConfidenceLevel confidence, string evidence, string steps) =>
        new($"{type}|{resource.Id}", type, resource.Id, $"{type} — {resource.Name}", savings, confidence, evidence, steps);

    private static decimal Half(decimal amount) => Round(amount / 2m);
    private static decimal Round(decimal amount) => decimal.Round(amount, 2, MidpointRounding.AwayFromZero);
}
