using FieldOps.Application;

namespace FieldOps.Infrastructure;

public sealed class TenantContext : IMutableTenantContext
{
    public Guid? TenantId { get; private set; }
    public string? TenantSlug { get; private set; }
    public bool IsResolved => TenantId.HasValue;
    public Guid RequiredTenantId => TenantId ?? throw new TenantNotResolvedException();

    public void Set(Guid tenantId, string slug)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant id cannot be empty.", nameof(tenantId));
        if (TenantId.HasValue && TenantId.Value != tenantId)
        {
            throw new CrossTenantAccessException("The tenant context cannot be changed within a request.");
        }

        TenantId = tenantId;
        TenantSlug = slug;
    }
}
