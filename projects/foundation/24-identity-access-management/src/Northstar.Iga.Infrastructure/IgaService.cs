using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Infrastructure;

public sealed partial class IgaService(
    IgaDbContext db,
    IClock clock,
    IEnumerable<IProvisioningConnector> connectors,
    IHrIdentitySource hrSource,
    IOptions<LifecycleOptions> lifecycleOptions,
    IOptions<GovernanceOptions> governanceOptions,
    IOptions<ProvisioningOptions> provisioningOptions,
    IAuditContextAccessor auditContext,
    ILogger<IgaService> logger) : IIgaService
{
    private readonly IgaDbContext _db = db;
    private readonly IClock _clock = clock;
    private readonly IReadOnlyDictionary<string, IProvisioningConnector> _connectors =
        connectors.ToDictionary(x => x.Key, StringComparer.OrdinalIgnoreCase);
    private readonly IHrIdentitySource _hrSource = hrSource;
    private readonly LifecycleOptions _lifecycleOptions = lifecycleOptions.Value;
    private readonly GovernanceOptions _governanceOptions = governanceOptions.Value;
    private readonly ProvisioningOptions _provisioningOptions = provisioningOptions.Value;
    private readonly IAuditContextAccessor _auditContext = auditContext;
    private readonly ILogger<IgaService> _logger = logger;

    private async Task<UserIdentity> RequireUserAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken)
        ?? throw new KeyNotFoundException($"Identity '{userId}' was not found.");

    private async Task<IReadOnlyList<AccessDerivation>> ResolveDerivationsAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var entitlements = await _db.Entitlements.AsNoTracking().ToListAsync(cancellationToken);
        var entitlementById = entitlements.ToDictionary(x => x.Id);
        var roles = await _db.Roles.AsNoTracking().ToListAsync(cancellationToken);
        var roleById = roles.ToDictionary(x => x.Id);
        var roleEntitlements = await _db.RoleEntitlements.AsNoTracking().ToListAsync(cancellationToken);
        var inheritances = await _db.RoleInheritances.AsNoTracking().ToListAsync(cancellationToken);
        var resolver = new RoleHierarchyResolver(inheritances);
        var exclusions = (await _db.UserEntitlementExclusions.AsNoTracking()
                .Where(x => x.UserId == userId)
                .ToListAsync(cancellationToken))
            .Select(x => x.EntitlementId)
            .ToHashSet();
        var results = new List<AccessDerivation>();

        var directEntitlements = (await _db.UserEntitlementGrants.AsNoTracking()
                .Where(x => x.UserId == userId && x.RevokedAt == null)
                .ToListAsync(cancellationToken))
            .Where(x => x.ExpiresAt is null || x.ExpiresAt > now);
        foreach (var grant in directEntitlements)
        {
            if (!entitlementById.TryGetValue(grant.EntitlementId, out var entitlement) ||
                exclusions.Contains(entitlement.Id))
            {
                continue;
            }

            results.Add(new AccessDerivation(
                entitlement.Id,
                entitlement.Permission,
                [$"{grant.Source.ToString().ToLowerInvariant()}-entitlement:{entitlement.Key}"],
                grant.ExpiresAt));
        }

        var directRoles = (await _db.UserRoleGrants.AsNoTracking()
                .Where(x => x.UserId == userId && x.RevokedAt == null)
                .ToListAsync(cancellationToken))
            .Where(x => x.ExpiresAt is null || x.ExpiresAt > now);
        foreach (var grant in directRoles)
        {
            AddRoleDerivations(
                grant.RoleId,
                [$"{grant.Source.ToString().ToLowerInvariant()}-role"],
                grant.ExpiresAt,
                resolver,
                roleEntitlements,
                roleById,
                entitlementById,
                exclusions,
                results);
        }

        var memberships = await _db.GroupMembers.AsNoTracking()
            .Where(x => x.UserId == userId)
            .ToListAsync(cancellationToken);
        if (memberships.Count > 0)
        {
            var groupIds = memberships.Select(x => x.GroupId).ToArray();
            var groups = await _db.Groups.AsNoTracking()
                .Where(x => groupIds.Contains(x.Id))
                .ToDictionaryAsync(x => x.Id, cancellationToken);
            var groupRoles = await _db.GroupRoles.AsNoTracking()
                .Where(x => groupIds.Contains(x.GroupId))
                .ToListAsync(cancellationToken);
            foreach (var membership in memberships)
            {
                if (!groups.TryGetValue(membership.GroupId, out var group))
                {
                    continue;
                }

                foreach (var groupRole in groupRoles.Where(x => x.GroupId == group.Id))
                {
                    AddRoleDerivations(
                        groupRole.RoleId,
                        [$"{membership.Source.ToString().ToLowerInvariant()}-group:{group.Key}", "group-role"],
                        null,
                        resolver,
                        roleEntitlements,
                        roleById,
                        entitlementById,
                        exclusions,
                        results);
                }
            }
        }

        var elevations = (await _db.Elevations.AsNoTracking()
                .Where(x => x.UserId == userId && x.Status == ElevationStatus.Active)
                .ToListAsync(cancellationToken))
            .Where(x => x.StartsAt <= now && x.EndsAt > now);
        foreach (var elevation in elevations)
        {
            if (!entitlementById.TryGetValue(elevation.EntitlementId, out var entitlement) ||
                exclusions.Contains(entitlement.Id))
            {
                continue;
            }

            results.Add(new AccessDerivation(
                entitlement.Id,
                entitlement.Permission,
                [$"jit-elevation:{elevation.TicketReference}", $"entitlement:{entitlement.Key}"],
                elevation.EndsAt));
        }

        return results
            .GroupBy(x => new { x.EntitlementId, Path = string.Join(">", x.Path), x.ExpiresAt })
            .Select(x => x.First())
            .ToArray();
    }

    private static void AddRoleDerivations(
        Guid roleId,
        IReadOnlyList<string> prefix,
        DateTimeOffset? expiresAt,
        RoleHierarchyResolver resolver,
        IReadOnlyCollection<RoleEntitlement> roleEntitlements,
        IReadOnlyDictionary<Guid, Role> roleById,
        IReadOnlyDictionary<Guid, Entitlement> entitlementById,
        IReadOnlySet<Guid> exclusions,
        ICollection<AccessDerivation> results)
    {
        foreach (var path in resolver.ResolveEntitlementPaths(roleId, roleEntitlements))
        {
            if (!entitlementById.TryGetValue(path.EntitlementId, out var entitlement) ||
                exclusions.Contains(entitlement.Id))
            {
                continue;
            }

            var rolePath = path.RoleIds
                .Select(id => roleById.TryGetValue(id, out var role) ? $"role:{role.Key}" : $"role:{id}")
                .ToArray();
            results.Add(new AccessDerivation(
                entitlement.Id,
                entitlement.Permission,
                prefix.Concat(rolePath).Concat([$"entitlement:{entitlement.Key}"]).ToArray(),
                expiresAt));
        }
    }

    private async Task<IReadOnlyList<Guid>> ResolveTargetEntitlementIdsAsync(
        RequestTargetType targetType,
        Guid targetId,
        CancellationToken cancellationToken)
    {
        if (targetType == RequestTargetType.Entitlement)
        {
            if (!await _db.Entitlements.AnyAsync(x => x.Id == targetId, cancellationToken))
            {
                throw new KeyNotFoundException($"Entitlement '{targetId}' was not found.");
            }

            return [targetId];
        }

        var roleIds = new List<Guid>();
        if (targetType == RequestTargetType.Role)
        {
            if (!await _db.Roles.AnyAsync(x => x.Id == targetId, cancellationToken))
            {
                throw new KeyNotFoundException($"Role '{targetId}' was not found.");
            }

            roleIds.Add(targetId);
        }
        else
        {
            if (!await _db.Groups.AnyAsync(x => x.Id == targetId, cancellationToken))
            {
                throw new KeyNotFoundException($"Group '{targetId}' was not found.");
            }

            roleIds.AddRange(await _db.GroupRoles.AsNoTracking()
                .Where(x => x.GroupId == targetId)
                .Select(x => x.RoleId)
                .ToListAsync(cancellationToken));
        }

        var inheritances = await _db.RoleInheritances.AsNoTracking().ToListAsync(cancellationToken);
        var roleEntitlements = await _db.RoleEntitlements.AsNoTracking().ToListAsync(cancellationToken);
        var resolver = new RoleHierarchyResolver(inheritances);
        return roleIds
            .SelectMany(roleId => resolver.ResolveEntitlementPaths(roleId, roleEntitlements))
            .Select(x => x.EntitlementId)
            .Distinct()
            .ToArray();
    }

    private async Task AppendAuditAsync(
        string actor,
        string action,
        string resourceType,
        string resourceId,
        string correlationId,
        object? before,
        object? after,
        object? details,
        CancellationToken cancellationToken)
    {
        var tracked = _db.ChangeTracker.Entries<AuditRecord>()
            .Where(x => x.State == EntityState.Added)
            .Select(x => x.Entity)
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefault();
        var persisted = await _db.AuditRecords.AsNoTracking()
            .OrderByDescending(x => x.Sequence)
            .FirstOrDefaultAsync(cancellationToken);
        var previous = tracked is not null && (persisted is null || tracked.Sequence > persisted.Sequence)
            ? tracked
            : persisted;
        var sequence = (previous?.Sequence ?? 0) + 1;
        var beforeHash = Hash(before);
        var afterHash = Hash(after);
        var detailsJson = JsonSerializer.Serialize(details ?? new { });
        var previousHash = previous?.RecordHash ?? string.Empty;
        var timestamp = _clock.UtcNow;
        var sourceIp = _auditContext.SourceIp;
        var userAgent = _auditContext.UserAgent;
        var recordHash = Hash(
            $"{sequence}|{timestamp:O}|{actor}|{action}|{resourceType}|{resourceId}|" +
            $"{correlationId}|{sourceIp}|{userAgent}|{beforeHash}|{afterHash}|{detailsJson}|{previousHash}");
        _db.AuditRecords.Add(new AuditRecord
        {
            Sequence = sequence,
            Timestamp = timestamp,
            Actor = actor,
            Action = action,
            ResourceType = resourceType,
            ResourceId = resourceId,
            CorrelationId = correlationId,
            SourceIp = sourceIp,
            UserAgent = userAgent,
            BeforeHash = beforeHash,
            AfterHash = afterHash,
            DetailsJson = detailsJson,
            PreviousHash = previousHash,
            RecordHash = recordHash
        });
        IgaTelemetry.AuditEvents.Add(1);
    }

    private static string Hash(object? value)
    {
        var text = value is string stringValue ? stringValue : JsonSerializer.Serialize(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text ?? string.Empty))).ToLowerInvariant();
    }

    private async Task RefreshTelemetryAsync(CancellationToken cancellationToken)
    {
        IgaTelemetry.SetPendingRequests(await _db.AccessRequests.AsNoTracking()
            .LongCountAsync(x => x.Status == AccessRequestStatus.Pending, cancellationToken));
        IgaTelemetry.SetActiveElevations(await _db.Elevations.AsNoTracking()
            .LongCountAsync(x => x.Status == ElevationStatus.Active, cancellationToken));
        var total = await _db.CertificationItems.AsNoTracking().LongCountAsync(cancellationToken);
        var complete = await _db.CertificationItems.AsNoTracking()
            .LongCountAsync(x => x.Decision != CertificationDecision.Pending, cancellationToken);
        IgaTelemetry.SetCampaignCompletion(complete, total);
    }
}
