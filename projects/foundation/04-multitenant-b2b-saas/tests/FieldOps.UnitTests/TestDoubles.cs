using System.Collections.Concurrent;
using FieldOps.Application;
using FieldOps.Domain;

namespace FieldOps.UnitTests;

internal sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = now;
    public void Advance(TimeSpan amount) => UtcNow = UtcNow.Add(amount);
    public void Set(DateTimeOffset value) => UtcNow = value;
}

internal sealed class FakeTenantContext(Guid tenantId, string slug = "tenant") : IMutableTenantContext
{
    public Guid? TenantId { get; private set; } = tenantId;
    public string? TenantSlug { get; private set; } = slug;
    public bool IsResolved => TenantId.HasValue;
    public Guid RequiredTenantId => TenantId ?? throw new TenantNotResolvedException();
    public void Set(Guid tenantId, string slug)
    {
        TenantId = tenantId;
        TenantSlug = slug;
    }
}

internal sealed class MemoryUsageStore : IUsageMeterStore
{
    private readonly ConcurrentDictionary<(Guid Tenant, string Metric, DateOnly Period), long> _values = new();

    public Task<long> IncrementAsync(
        Guid tenantId,
        string metric,
        DateOnly periodStart,
        long amount,
        CancellationToken cancellationToken) =>
        Task.FromResult(_values.AddOrUpdate((tenantId, metric, periodStart), amount, (_, value) => value + amount));

    public Task<long> GetAsync(
        Guid tenantId,
        string metric,
        DateOnly periodStart,
        CancellationToken cancellationToken) =>
        Task.FromResult(_values.GetValueOrDefault((tenantId, metric, periodStart)));
}

internal sealed class MemoryFlagStore : IFeatureFlagStore
{
    public Dictionary<string, FeatureFlag> Flags { get; } = new(StringComparer.Ordinal);
    public Dictionary<(string Key, Guid User), FeatureFlagOverride> Overrides { get; } = new();

    public Task<FeatureFlag?> FindAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Flags.GetValueOrDefault(key.ToLowerInvariant()));

    public Task<FeatureFlagOverride?> FindOverrideAsync(string key, Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(Overrides.GetValueOrDefault((key.ToLowerInvariant(), userId)));

    public Task UpsertAsync(FeatureFlag flag, CancellationToken cancellationToken)
    {
        Flags[flag.Key] = flag;
        return Task.CompletedTask;
    }

    public Task UpsertOverrideAsync(FeatureFlagOverride value, CancellationToken cancellationToken)
    {
        Overrides[(value.FlagKey, value.UserId)] = value;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<FeatureFlag>>(Flags.Values.ToList());

    public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class RecordingAuditWriter : IAuditWriter
{
    public List<(string Action, string Resource)> Writes { get; } = [];

    public Task WriteAsync(
        string actorId,
        string action,
        string resource,
        object? before,
        object? after,
        string correlationId,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        Writes.Add((action, resource));
        return Task.CompletedTask;
    }
}

internal sealed class MemoryMembershipRepository(params Membership[] memberships) : IMembershipRepository
{
    private readonly List<Membership> _memberships = [.. memberships];
    public int SaveCount { get; private set; }

    public Task<Membership?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.SingleOrDefault(x => x.Id == id));

    public Task<Membership?> FindByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.SingleOrDefault(x => x.UserId == userId));

    public Task<bool> UserBelongsToTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.Any(x => x.UserId == userId && x.TenantId == tenantId && x.IsActive));

    public Task<int> CountActiveAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_memberships.Count(x => x.IsActive));

    public Task AddAsync(Membership membership, CancellationToken cancellationToken)
    {
        _memberships.Add(membership);
        return Task.CompletedTask;
    }

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

internal sealed class MemoryInvitationRepository : IInvitationRepository
{
    private readonly List<(Invitation Invitation, string Token)> _invitations = [];

    public Task<Invitation?> FindByTokenAsync(string rawToken, CancellationToken cancellationToken) =>
        Task.FromResult(_invitations.Select(x => x.Invitation).SingleOrDefault(x => x.Matches(rawToken)));

    public Task AddAsync(Invitation invitation, CancellationToken cancellationToken)
    {
        throw new NotSupportedException("Use AddKnown for deterministic invitation tests.");
    }

    public void AddKnown(Invitation invitation, string token) => _invitations.Add((invitation, token));
    public Task SaveAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class MemoryOrganizationRepository(params Organization[] organizations) : IOrganizationRepository
{
    public IReadOnlyList<Organization> Organizations { get; } = organizations;
    public int SaveCount { get; private set; }

    public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        Task.FromResult(Organizations.SingleOrDefault(x => x.Id == id));

    public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
        Task.FromResult(Organizations.SingleOrDefault(x => x.Slug == slug.ToLowerInvariant()));

    public Task<IReadOnlyList<Organization>> ListAllAsync(CancellationToken cancellationToken) =>
        Task.FromResult(Organizations);

    public Task SaveAsync(CancellationToken cancellationToken)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

internal sealed class MemoryReceiptStore : IWebhookReceiptStore
{
    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    public Task<bool> TryRecordAsync(string eventId, string eventType, DateTimeOffset processedAt, CancellationToken cancellationToken) =>
        Task.FromResult(_ids.Add(eventId));
}

internal sealed class NullObjectStore : IObjectStore
{
    public Task<StoredObject> PutAsync(string fileName, string contentType, Stream content, long length, CancellationToken cancellationToken) =>
        Task.FromResult(new StoredObject($"obj://test/{fileName}", fileName, contentType, length));

    public Task<Stream> OpenReadAsync(string objectKey, CancellationToken cancellationToken) =>
        Task.FromResult<Stream>(new MemoryStream());
}
