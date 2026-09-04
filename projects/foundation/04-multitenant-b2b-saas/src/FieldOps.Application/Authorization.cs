using FieldOps.Domain;

namespace FieldOps.Application;

public static class Permissions
{
    public const string JobsCreate = "jobs:create";
    public const string JobsAssign = "jobs:assign";
    public const string JobsComplete = "jobs:complete";
    public const string AssetsManage = "assets:manage";
    public const string InspectionsSubmit = "inspections:submit";
    public const string BillingManage = "billing:manage";
    public const string MembersManage = "members:manage";
    public const string AuditRead = "audit:read";

    public static readonly string[] All =
    [
        JobsCreate, JobsAssign, JobsComplete, AssetsManage, InspectionsSubmit,
        BillingManage, MembersManage, AuditRead
    ];
}

public static class RolePermissions
{
    private static readonly IReadOnlyDictionary<MemberRole, IReadOnlySet<string>> Matrix =
        new Dictionary<MemberRole, IReadOnlySet<string>>
        {
            [MemberRole.Owner] = Permissions.All.ToHashSet(StringComparer.Ordinal),
            [MemberRole.Admin] = Permissions.All.ToHashSet(StringComparer.Ordinal),
            [MemberRole.Dispatcher] = new HashSet<string>(
                [Permissions.JobsCreate, Permissions.JobsAssign, Permissions.AssetsManage, Permissions.AuditRead],
                StringComparer.Ordinal),
            [MemberRole.Technician] = new HashSet<string>(
                [Permissions.JobsComplete, Permissions.InspectionsSubmit],
                StringComparer.Ordinal),
            [MemberRole.Viewer] = new HashSet<string>(StringComparer.Ordinal)
        };

    public static IReadOnlySet<string> For(MemberRole role) => Matrix[role];
    public static bool Allows(MemberRole role, string permission) => Matrix[role].Contains(permission);
}

public interface IPermissionService
{
    Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken);
    Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken);
    Task ChangeRoleAsync(Guid membershipId, MemberRole role, CancellationToken cancellationToken);
}

public sealed class PermissionService(
    ITenantContext tenantContext,
    IMembershipRepository memberships,
    IAppCache cache) : IPermissionService
{
    public async Task<bool> HasPermissionAsync(Guid userId, string permission, CancellationToken cancellationToken)
    {
        var permissions = await GetPermissionsAsync(userId, cancellationToken);
        return permissions.Contains(permission);
    }

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        _ = tenantContext.RequiredTenantId;
        var cacheKey = CacheKey(userId);
        var cached = await cache.GetAsync<string[]>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            return cached.ToHashSet(StringComparer.Ordinal);
        }

        var membership = await memberships.FindByUserAsync(userId, cancellationToken);
        var permissions = membership is { IsActive: true }
            ? RolePermissions.For(membership.Role)
            : new HashSet<string>(StringComparer.Ordinal);
        await cache.SetAsync(cacheKey, permissions.ToArray(), TimeSpan.FromMinutes(5), cancellationToken);
        return permissions;
    }

    public async Task ChangeRoleAsync(Guid membershipId, MemberRole role, CancellationToken cancellationToken)
    {
        var membership = await memberships.FindAsync(membershipId, cancellationToken)
            ?? throw new KeyNotFoundException("Membership was not found.");
        TenantGuard.EnsureSameTenant(tenantContext.RequiredTenantId, membership);
        membership.ChangeRole(role);
        await memberships.SaveAsync(cancellationToken);
        await cache.RemoveAsync(CacheKey(membership.UserId), cancellationToken);
    }

    private static string CacheKey(Guid userId) => $"permissions:{userId:N}";
}
