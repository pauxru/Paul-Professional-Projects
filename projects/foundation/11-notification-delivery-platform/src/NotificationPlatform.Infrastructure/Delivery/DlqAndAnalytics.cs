namespace NotificationPlatform.Infrastructure.Delivery;

using Microsoft.EntityFrameworkCore;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Analytics;
using NotificationPlatform.Application.Dlq;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Persistence;
using NotificationPlatform.Infrastructure.Providers.Common;

public sealed class DlqService : IDlqService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IClock _clock;

    public DlqService(IDbContextFactory<AppDbContext> dbFactory, IClock clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    public async Task<IReadOnlyList<DlqItemDto>> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Notifications.AsNoTracking()
            .Where(n => n.TenantId == tenantId && n.Status == NotificationStatus.DeadLettered)
            .OrderByDescending(n => n.FailedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize);
        var list = await q.ToListAsync(ct).ConfigureAwait(false);
        return list.Select(n => new DlqItemDto(n.Id, n.TemplateKey, n.Channel, n.Category, n.LastError, n.LastProvider, n.AttemptCount, n.CreatedAt, n.FailedAt)).ToList();
    }

    public async Task<int> CountAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Notifications.CountAsync(n => n.TenantId == tenantId && n.Status == NotificationStatus.DeadLettered, ct).ConfigureAwait(false);
    }

    public async Task<DlqReplayResult> ReplayAsync(Guid tenantId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var items = await db.Notifications.Where(n => n.TenantId == tenantId && ids.Contains(n.Id) && n.Status == NotificationStatus.DeadLettered).ToListAsync(ct).ConfigureAwait(false);
        int replayed = 0;
        foreach (var n in items)
        {
            n.MarkQueued();
            replayed++;
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new DlqReplayResult(replayed, ids.Count - replayed);
    }
}

public sealed class AnalyticsService : IAnalyticsService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IProviderHealthTracker _health;

    public AnalyticsService(IDbContextFactory<AppDbContext> dbFactory, IProviderHealthTracker health)
    {
        _dbFactory = dbFactory;
        _health = health;
    }

    public async Task<TenantAnalyticsSummary> GetSummaryAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Notifications.AsNoTracking().Where(n => n.TenantId == tenantId && n.CreatedAt >= from && n.CreatedAt < to);
        var totals = await q.GroupBy(n => n.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        int Total(NotificationStatus s) => totals.SingleOrDefault(x => x.Key == s)?.Count ?? 0;
        int total = totals.Sum(x => x.Count);
        int delivered = Total(NotificationStatus.Delivered);
        int bounced = Total(NotificationStatus.Bounced);
        int failed = Total(NotificationStatus.Failed);
        int suppressed = Total(NotificationStatus.Suppressed);
        int dead = Total(NotificationStatus.DeadLettered);
        double deliveryRate = total == 0 ? 0 : delivered / (double)total;
        double bounceRate = total == 0 ? 0 : bounced / (double)total;

        // Latency: use attempts as proxy: max attempt latency for delivered notifications
        var byProvider = await q.Where(n => !string.IsNullOrEmpty(n.LastProvider))
            .GroupBy(n => n.LastProvider!)
            .Select(g => new { Provider = g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var byChannel = await q.GroupBy(n => n.Channel)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);

        var latencies = await db.Attempts.AsNoTracking()
            .Join(db.Notifications.Where(n => n.TenantId == tenantId && n.CreatedAt >= from && n.CreatedAt < to && n.Status == NotificationStatus.Delivered),
                  a => a.NotificationId, n => n.Id, (a, n) => a.LatencyMilliseconds)
            .ToListAsync(ct).ConfigureAwait(false);
        latencies.Sort();
        int Percentile(int p)
        {
            if (latencies.Count == 0) return 0;
            var idx = (int)Math.Clamp(Math.Ceiling(p / 100.0 * latencies.Count) - 1, 0, latencies.Count - 1);
            return latencies[idx];
        }

        return new TenantAnalyticsSummary(
            total, delivered, bounced, failed, suppressed, dead,
            deliveryRate, bounceRate,
            Percentile(50), Percentile(95), Percentile(99),
            byProvider.ToDictionary(x => x.Provider, x => x.Count),
            byChannel.ToDictionary(x => x.Key, x => x.Count));
    }

    public async Task<IReadOnlyList<ProviderHealthDto>> GetProviderHealthAsync(CancellationToken ct)
    {
        var list = await _health.ListAsync(ct).ConfigureAwait(false);
        return list.Select(h => new ProviderHealthDto(h.ProviderName, h.Channel, h.State, h.ConsecutiveFailures, h.LastFailureAt)).ToList();
    }

    public async Task<QueueSnapshot> GetQueueSnapshotAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var groups = await db.Notifications.AsNoTracking().GroupBy(n => n.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        int C(NotificationStatus s) => groups.SingleOrDefault(x => x.Key == s)?.Count ?? 0;
        return new QueueSnapshot(
            C(NotificationStatus.Queued),
            C(NotificationStatus.Scheduled),
            C(NotificationStatus.Rendering),
            C(NotificationStatus.Dispatched),
            C(NotificationStatus.Sent),
            C(NotificationStatus.Delivered),
            C(NotificationStatus.Bounced),
            C(NotificationStatus.Failed),
            C(NotificationStatus.Suppressed),
            C(NotificationStatus.DeadLettered));
    }
}
