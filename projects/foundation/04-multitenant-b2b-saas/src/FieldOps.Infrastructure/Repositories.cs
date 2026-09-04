using System.Collections.Concurrent;
using FieldOps.Application;
using FieldOps.Domain;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Infrastructure;

public sealed class JobRepository(FieldOpsDbContext dbContext) : IJobRepository
{
    public Task<Job?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Jobs.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task<IReadOnlyList<Job>> ListAsync(
        int skip,
        int take,
        JobStatus? status,
        string? sort,
        CancellationToken cancellationToken)
    {
        var query = dbContext.Jobs.AsNoTracking();
        if (status.HasValue) query = query.Where(x => x.Status == status);
        query = sort?.ToLowerInvariant() switch
        {
            "priority" => query.OrderByDescending(x => x.Priority).ThenBy(x => x.ScheduleStart),
            "sla" => query.OrderBy(x => x.SlaDueAt),
            "-created" => query.OrderByDescending(x => x.CreatedAt),
            _ => query.OrderBy(x => x.ScheduleStart)
        };
        return await query.Skip(skip).Take(take).ToListAsync(cancellationToken);
    }

    public Task<int> CountAsync(JobStatus? status, CancellationToken cancellationToken) =>
        status.HasValue
            ? dbContext.Jobs.CountAsync(x => x.Status == status, cancellationToken)
            : dbContext.Jobs.CountAsync(cancellationToken);

    public async Task AddAsync(Job job, CancellationToken cancellationToken) =>
        await dbContext.Jobs.AddAsync(job, cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class UnsafeJobRepository(FieldOpsDbContext dbContext)
{
    public Task<Job?> FindIgnoringFiltersAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Jobs.IgnoreQueryFilters().SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class AssetRepository(FieldOpsDbContext dbContext) : IAssetRepository
{
    public Task<Asset?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Assets.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) =>
        dbContext.Assets.CountAsync(cancellationToken);

    public async Task<IReadOnlyList<Asset>> ListAsync(int skip, int take, CancellationToken cancellationToken) =>
        await dbContext.Assets.AsNoTracking().OrderBy(x => x.AssetTag).Skip(skip).Take(take).ToListAsync(cancellationToken);

    public async Task AddAsync(Asset asset, CancellationToken cancellationToken) =>
        await dbContext.Assets.AddAsync(asset, cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class OrganizationRepository(FieldOpsDbContext dbContext) : IOrganizationRepository
{
    public Task<Organization?> FindByIdAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Organizations.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Organization?> FindBySlugAsync(string slug, CancellationToken cancellationToken) =>
        dbContext.Organizations.SingleOrDefaultAsync(x => x.Slug == slug.ToLower(), cancellationToken);

    public async Task<IReadOnlyList<Organization>> ListAllAsync(CancellationToken cancellationToken) =>
        await dbContext.Organizations.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class MembershipRepository(FieldOpsDbContext dbContext) : IMembershipRepository
{
    public Task<Membership?> FindAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Memberships.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public Task<Membership?> FindByUserAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.Memberships.SingleOrDefaultAsync(x => x.UserId == userId, cancellationToken);

    public Task<bool> UserBelongsToTenantAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken) =>
        dbContext.Memberships.IgnoreQueryFilters().AnyAsync(
            x => x.UserId == userId && x.TenantId == tenantId && x.IsActive,
            cancellationToken);

    public Task<int> CountActiveAsync(CancellationToken cancellationToken) =>
        dbContext.Memberships.CountAsync(x => x.IsActive, cancellationToken);

    public async Task AddAsync(Membership membership, CancellationToken cancellationToken) =>
        await dbContext.Memberships.AddAsync(membership, cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class InvitationRepository(FieldOpsDbContext dbContext) : IInvitationRepository
{
    public async Task<Invitation?> FindByTokenAsync(string rawToken, CancellationToken cancellationToken)
    {
        var candidates = await dbContext.Invitations.Where(x => x.AcceptedAt == null).ToListAsync(cancellationToken);
        return candidates.SingleOrDefault(x => x.Matches(rawToken));
    }

    public async Task AddAsync(Invitation invitation, CancellationToken cancellationToken) =>
        await dbContext.Invitations.AddAsync(invitation, cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class InspectionRepository(FieldOpsDbContext dbContext) : IInspectionRepository
{
    public Task<InspectionTemplate?> FindTemplateAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.InspectionTemplates.Include(x => x.Items).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);

    public async Task AddTemplateAsync(InspectionTemplate template, CancellationToken cancellationToken) =>
        await dbContext.InspectionTemplates.AddAsync(template, cancellationToken);

    public async Task AddSubmissionAsync(InspectionSubmission submission, CancellationToken cancellationToken) =>
        await dbContext.InspectionSubmissions.AddAsync(submission, cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class FeatureFlagStore(FieldOpsDbContext dbContext) : IFeatureFlagStore
{
    public Task<FeatureFlag?> FindAsync(string key, CancellationToken cancellationToken) =>
        dbContext.FeatureFlags.SingleOrDefaultAsync(x => x.Key == key.ToLower(), cancellationToken);

    public Task<FeatureFlagOverride?> FindOverrideAsync(string key, Guid userId, CancellationToken cancellationToken) =>
        dbContext.FeatureFlagOverrides.SingleOrDefaultAsync(
            x => x.FlagKey == key.ToLower() && x.UserId == userId,
            cancellationToken);

    public async Task UpsertAsync(FeatureFlag flag, CancellationToken cancellationToken)
    {
        if (dbContext.Entry(flag).State == EntityState.Detached)
        {
            await dbContext.FeatureFlags.AddAsync(flag, cancellationToken);
        }
    }

    public async Task UpsertOverrideAsync(FeatureFlagOverride value, CancellationToken cancellationToken)
    {
        if (dbContext.Entry(value).State == EntityState.Detached)
        {
            await dbContext.FeatureFlagOverrides.AddAsync(value, cancellationToken);
        }
    }

    public async Task<IReadOnlyList<FeatureFlag>> ListAsync(CancellationToken cancellationToken) =>
        await dbContext.FeatureFlags.AsNoTracking().OrderBy(x => x.Key).ToListAsync(cancellationToken);

    public Task SaveAsync(CancellationToken cancellationToken) => dbContext.SaveChangesAsync(cancellationToken);
}

public sealed class UsageMeterStore(FieldOpsDbContext dbContext) : IUsageMeterStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    public async Task<long> IncrementAsync(
        Guid tenantId,
        string metric,
        DateOnly periodStart,
        long amount,
        CancellationToken cancellationToken)
    {
        var key = $"{tenantId:N}:{metric}:{periodStart:yyyy-MM-dd}";
        var gate = Locks.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var counter = await dbContext.UsageCounters.SingleOrDefaultAsync(
                x => x.TenantId == tenantId && x.Metric == metric && x.PeriodStart == periodStart,
                cancellationToken);
            if (counter is null)
            {
                counter = new UsageCounter(tenantId, metric, periodStart);
                await dbContext.UsageCounters.AddAsync(counter, cancellationToken);
            }

            var value = counter.Increment(amount);
            await dbContext.SaveChangesAsync(cancellationToken);
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<long> GetAsync(
        Guid tenantId,
        string metric,
        DateOnly periodStart,
        CancellationToken cancellationToken) =>
        (await dbContext.UsageCounters.AsNoTracking().SingleOrDefaultAsync(
            x => x.TenantId == tenantId && x.Metric == metric && x.PeriodStart == periodStart,
            cancellationToken))?.Value ?? 0;
}

public sealed class WebhookReceiptStore(FieldOpsDbContext dbContext) : IWebhookReceiptStore
{
    public async Task<bool> TryRecordAsync(
        string eventId,
        string eventType,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken)
    {
        if (await dbContext.WebhookReceipts.AnyAsync(x => x.EventId == eventId, cancellationToken)) return false;
        await dbContext.WebhookReceipts.AddAsync(new WebhookReceipt(eventId, eventType, processedAt), cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            return false;
        }
    }
}
