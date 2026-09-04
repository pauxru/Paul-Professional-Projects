using FieldOps.Domain;

namespace FieldOps.Application;

public sealed record PlanDefinition(
    SubscriptionPlan Plan,
    int MaxUsers,
    int MaxAssets,
    int MaxJobsPerMonth,
    int ApiRequestsPerMinute,
    int RetentionDays,
    IReadOnlySet<string> Features);

public static class PlanCatalog
{
    public static readonly IReadOnlyDictionary<SubscriptionPlan, PlanDefinition> Plans =
        new Dictionary<SubscriptionPlan, PlanDefinition>
        {
            [SubscriptionPlan.Free] = new(
                SubscriptionPlan.Free, 3, 10, 25, 30, 30,
                new HashSet<string>(["jobs", "assets"], StringComparer.Ordinal)),
            [SubscriptionPlan.Starter] = new(
                SubscriptionPlan.Starter, 10, 100, 500, 120, 180,
                new HashSet<string>(["jobs", "assets", "inspections"], StringComparer.Ordinal)),
            [SubscriptionPlan.Professional] = new(
                SubscriptionPlan.Professional, 50, 2_000, 10_000, 600, 730,
                new HashSet<string>(["jobs", "assets", "inspections", "feature-flags", "audit-export"], StringComparer.Ordinal)),
            [SubscriptionPlan.Enterprise] = new(
                SubscriptionPlan.Enterprise, 10_000, 1_000_000, 10_000_000, 5_000, 2_555,
                new HashSet<string>(
                    ["jobs", "assets", "inspections", "feature-flags", "audit-export", "sso", "custom-retention"],
                    StringComparer.Ordinal))
        };
}

public interface IEntitlementService
{
    PlanDefinition GetPlan(SubscriptionPlan plan);
    void EnsureFeature(SubscriptionPlan plan, string feature);
    void EnsureLimit(string metric, long current, long requested, long limit, bool rateLimit = false);
}

public sealed class EntitlementService : IEntitlementService
{
    public PlanDefinition GetPlan(SubscriptionPlan plan) => PlanCatalog.Plans[plan];

    public void EnsureFeature(SubscriptionPlan plan, string feature)
    {
        if (!GetPlan(plan).Features.Contains(feature))
        {
            throw new EntitlementDeniedException(feature, plan);
        }
    }

    public void EnsureLimit(string metric, long current, long requested, long limit, bool rateLimit = false)
    {
        if (current + requested > limit)
        {
            throw new QuotaExceededException(metric, limit, rateLimit);
        }
    }
}

public sealed record UsageDecision(long Value, long SoftLimit, long HardLimit, bool SoftLimitReached);

public sealed class UsageQuotaService(IUsageMeterStore store, IClock clock)
{
    public DateOnly CurrentMonthlyPeriod => new(clock.UtcNow.Year, clock.UtcNow.Month, 1);

    public async Task<UsageDecision> IncrementMonthlyAsync(
        Guid tenantId,
        string metric,
        long amount,
        long softLimit,
        long hardLimit,
        bool rateLimit,
        CancellationToken cancellationToken)
    {
        var period = CurrentMonthlyPeriod;
        var current = await store.GetAsync(tenantId, metric, period, cancellationToken);
        if (current + amount > hardLimit)
        {
            throw new QuotaExceededException(metric, hardLimit, rateLimit);
        }

        var value = await store.IncrementAsync(tenantId, metric, period, amount, cancellationToken);
        return new UsageDecision(value, softLimit, hardLimit, value >= softLimit);
    }

    public Task<long> GetMonthlyAsync(Guid tenantId, string metric, CancellationToken cancellationToken) =>
        store.GetAsync(tenantId, metric, CurrentMonthlyPeriod, cancellationToken);
}
