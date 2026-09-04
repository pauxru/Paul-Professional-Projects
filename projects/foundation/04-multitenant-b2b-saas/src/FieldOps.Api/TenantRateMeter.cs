using System.Collections.Concurrent;
using FieldOps.Application;

namespace FieldOps.Api;

public sealed class TenantRequestMeter(IClock clock)
{
    private readonly ConcurrentDictionary<(Guid TenantId, long Minute), long> _counts = new();

    public long Increment(Guid tenantId)
    {
        var minute = CurrentMinute;
        return _counts.AddOrUpdate((tenantId, minute), 1, static (_, current) => current + 1);
    }

    public long Current(Guid tenantId) => _counts.GetValueOrDefault((tenantId, CurrentMinute));
    private long CurrentMinute => clock.UtcNow.ToUnixTimeSeconds() / 60;
}

public sealed class TenantPlanRateLimitMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        ITenantContext tenant,
        IOrganizationRepository organizations,
        IEntitlementService entitlements,
        TenantRequestMeter meter)
    {
        if (tenant.IsResolved)
        {
            var organization = await organizations.FindByIdAsync(tenant.RequiredTenantId, context.RequestAborted)
                ?? throw new KeyNotFoundException("Organization was not found.");
            var limit = entitlements.GetPlan(organization.Plan).ApiRequestsPerMinute;
            if (meter.Increment(tenant.RequiredTenantId) > limit)
            {
                throw new QuotaExceededException("api-requests-per-minute", limit, true);
            }
        }

        await next(context);
    }
}
