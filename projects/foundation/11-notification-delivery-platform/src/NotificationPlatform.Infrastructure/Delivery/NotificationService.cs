namespace NotificationPlatform.Infrastructure.Delivery;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Application.Options;
using NotificationPlatform.Application.Fairness;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Domain.Notifications;
using NotificationPlatform.Domain.Recipients;
using NotificationPlatform.Domain.Suppressions;
using NotificationPlatform.Infrastructure.Persistence;
using NotificationPlatform.Infrastructure.Metrics;

public sealed class NotificationService : INotificationService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly IQuietHoursCalculator _quietHours;
    private readonly NotificationOptions _options;
    private readonly INotificationMetrics _metrics;
    private readonly ILogger<NotificationService> _log;

    public NotificationService(
        IDbContextFactory<AppDbContext> dbFactory,
        IClock clock,
        IIdGenerator ids,
        IQuietHoursCalculator quietHours,
        IOptions<NotificationOptions> options,
        INotificationMetrics metrics,
        ILogger<NotificationService> log)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _ids = ids;
        _quietHours = quietHours;
        _options = options.Value;
        _metrics = metrics;
        _log = log;
    }

    public async Task<SendOutcome> QueueAsync(Guid tenantId, SendNotificationRequest request, string correlationId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var tenant = await db.Tenants.AsNoTracking().SingleOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        if (tenant is null) return new SendOutcome(SendOutcomeKind.Rejected, null, "tenant_not_found", null);

        var recipient = await db.Recipients.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.ExternalId == request.RecipientExternalId, ct).ConfigureAwait(false);
        if (recipient is null) return new SendOutcome(SendOutcomeKind.Rejected, null, "recipient_not_found", null);
        if (!recipient.HasChannel(request.Channel))
            return new SendOutcome(SendOutcomeKind.Rejected, null, $"recipient_missing_channel:{request.Channel}", null);

        var payloadJson = request.Payload is null ? "{}" : request.Payload.Value.GetRawText();

        // Idempotency: if idempotency key present and matches previous request, replay original response
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var reqHash = HashRequest(request, payloadJson);
            var idem = await db.Idempotency.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Key == request.IdempotencyKey, ct).ConfigureAwait(false);
            if (idem is not null)
            {
                var previous = JsonSerializer.Deserialize<SendOutcome>(idem.ResponseJson);
                if (previous is null) return new SendOutcome(SendOutcomeKind.Rejected, null, "idempotency_replay_failed", null);
                return previous with { Kind = SendOutcomeKind.IdempotentReplay };
            }
        }

        // Preference check for marketing
        var pref = await db.Preferences.SingleOrDefaultAsync(p => p.TenantId == tenantId && p.RecipientId == recipient.Id && p.Channel == request.Channel && p.Category == request.Category, ct).ConfigureAwait(false);
        if (pref is not null && !pref.OptedIn)
        {
            var outcome = new SendOutcome(SendOutcomeKind.Suppressed, null, "opted_out", NotificationStatus.Suppressed);
            _metrics.RecordSuppressed(tenant.Slug, request.Channel.ToString());
            return outcome;
        }

        // Suppression list check
        var address = recipient.AddressFor(request.Channel)!;
        var normalized = SuppressionEntry.NormalizeAddress(request.Channel, address);
        var suppressed = await db.Suppressions.AnyAsync(s => s.TenantId == tenantId && s.Channel == request.Channel && s.Address == normalized, ct).ConfigureAwait(false);
        if (suppressed)
        {
            var outcome = new SendOutcome(SendOutcomeKind.Suppressed, null, "suppressed_address", NotificationStatus.Suppressed);
            _metrics.RecordSuppressed(tenant.Slug, request.Channel.ToString());
            return outcome;
        }

        // Deduplication window
        if (!string.IsNullOrWhiteSpace(request.DeduplicationKey))
        {
            var since = _clock.UtcNow.AddMinutes(-_options.DedupWindowMinutes);
            var dup = await db.Notifications.AnyAsync(n => n.TenantId == tenantId
                && n.RecipientId == recipient.Id
                && n.TemplateKey == request.TemplateKey
                && n.DeduplicationKey == request.DeduplicationKey
                && n.CreatedAt >= since, ct).ConfigureAwait(false);
            if (dup)
            {
                var outcome = new SendOutcome(SendOutcomeKind.Deduplicated, null, "dedup_window_hit", null);
                return outcome;
            }
        }

        // Frequency capping for Marketing
        if (request.Category == NotificationCategory.Marketing)
        {
            var dateKey = TodayInRecipientTz(recipient);
            var counter = await db.FrequencyCounters.SingleOrDefaultAsync(c => c.TenantId == tenantId && c.RecipientId == recipient.Id && c.Category == request.Category && c.DateKey == dateKey, ct).ConfigureAwait(false);
            if (counter is not null && counter.Count >= _options.FrequencyCapPerDay && request.Priority != NotificationPriority.Transactional)
            {
                var outcome = new SendOutcome(SendOutcomeKind.Suppressed, null, "frequency_cap", NotificationStatus.Suppressed);
                _metrics.RecordSuppressed(tenant.Slug, request.Channel.ToString());
                return outcome;
            }
        }

        // Quota check
        var ym = _clock.UtcNow.Year * 100 + _clock.UtcNow.Month;
        var usage = await db.UsageCounters.SingleOrDefaultAsync(u => u.TenantId == tenantId && u.YearMonth == ym, ct).ConfigureAwait(false);
        var count = usage?.Count ?? 0;
        if (count >= tenant.MonthlyQuotaHard)
        {
            var outcome = new SendOutcome(SendOutcomeKind.Rejected, null, "monthly_quota_hard_limit", null);
            return outcome;
        }

        // Quiet hours -> compute scheduled time
        var now = _clock.UtcNow;
        DateTimeOffset? scheduled = request.SendAt.HasValue && request.SendAt.Value > now ? request.SendAt : null;
        if (recipient.HasQuietHours())
        {
            var basis = scheduled ?? now;
            var release = _quietHours.ComputeDeferralTarget(basis, recipient.TimeZoneId, recipient.QuietHoursStart, recipient.QuietHoursEnd, request.Priority);
            if (release > basis)
                scheduled = release;
        }

        // Resolve locale (used for template rendering; keep on notification)
        var locale = request.Locale ?? recipient.Locale ?? tenant.DefaultLocale;

        var notification = new Notification(
            _ids.NewId(),
            tenantId,
            recipient.Id,
            request.TemplateKey,
            request.Channel,
            request.Category,
            request.Priority,
            payloadJson,
            locale,
            request.IdempotencyKey,
            request.DeduplicationKey,
            now,
            scheduled,
            _options.DefaultMaxAttempts,
            correlationId);

        db.Notifications.Add(notification);

        if (usage is null)
        {
            usage = new NotificationPlatform.Domain.Providers.TenantUsageCounter(tenantId, ym, 1);
            db.UsageCounters.Add(usage);
        }
        else
        {
            usage.Increment();
        }

        var replay = new SendOutcome(SendOutcomeKind.Accepted, notification.Id, null, notification.Status);
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            var record = new IdempotencyRecord(
                _ids.NewId(),
                tenantId,
                request.IdempotencyKey!,
                HashRequest(request, payloadJson),
                JsonSerializer.Serialize(replay),
                200,
                now);
            db.Idempotency.Add(record);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _metrics.RecordQueued(tenant.Slug, request.Channel.ToString());
        _log.LogInformation("Queued notification {NotificationId} tenant={Tenant} channel={Channel} scheduled={Scheduled}", notification.Id, tenant.Slug, request.Channel, scheduled);
        return replay;
    }

    public async Task<BulkSendOutcome> QueueBulkAsync(Guid tenantId, BulkSendRequest request, string correlationId, CancellationToken ct)
    {
        if (request.Items.Count == 0) return new BulkSendOutcome(Array.Empty<SendOutcome>());
        if (request.Items.Count > _options.BulkMaxItems)
            throw new DomainException($"bulk exceeds max {_options.BulkMaxItems}");

        // Bulk-level idempotency: replay the whole batch outcome.
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            await using var dbCheck = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var idem = await dbCheck.Idempotency.SingleOrDefaultAsync(i => i.TenantId == tenantId && i.Key == request.IdempotencyKey, ct).ConfigureAwait(false);
            if (idem is not null)
            {
                var previous = JsonSerializer.Deserialize<BulkSendOutcome>(idem.ResponseJson);
                if (previous is not null)
                {
                    return new BulkSendOutcome(previous.Results.Select(r => r with { Kind = SendOutcomeKind.IdempotentReplay }).ToList());
                }
            }
        }

        var results = new List<SendOutcome>(request.Items.Count);
        foreach (var item in request.Items)
        {
            var single = new SendNotificationRequest(
                item.TemplateKey,
                item.Channel,
                item.RecipientExternalId,
                item.Payload,
                item.Priority,
                item.Category,
                item.Locale,
                item.SendAt,
                IdempotencyKey: null,
                item.DeduplicationKey);
            var outcome = await QueueAsync(tenantId, single, correlationId, ct).ConfigureAwait(false);
            results.Add(outcome);
        }

        var response = new BulkSendOutcome(results);
        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            await using var dbW = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            var already = await dbW.Idempotency.AnyAsync(i => i.TenantId == tenantId && i.Key == request.IdempotencyKey, ct).ConfigureAwait(false);
            if (!already)
            {
                var record = new IdempotencyRecord(
                    _ids.NewId(),
                    tenantId,
                    request.IdempotencyKey!,
                    "bulk",
                    JsonSerializer.Serialize(response),
                    200,
                    _clock.UtcNow);
                dbW.Idempotency.Add(record);
                await dbW.SaveChangesAsync(ct).ConfigureAwait(false);
            }
        }
        return response;
    }

    public async Task<NotificationDto?> GetAsync(Guid tenantId, Guid notificationId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var n = await db.Notifications.AsNoTracking().SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == notificationId, ct).ConfigureAwait(false);
        return n is null ? null : Map(n);
    }

    public async Task<IReadOnlyList<NotificationDto>> ListAsync(Guid tenantId, int page, int pageSize, NotificationStatus? status, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Notifications.AsNoTracking().Where(n => n.TenantId == tenantId);
        if (status.HasValue) q = q.Where(n => n.Status == status.Value);
        var list = await q.OrderByDescending(n => n.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        return list.Select(Map).ToList();
    }

    public async Task<int> CountAsync(Guid tenantId, NotificationStatus? status, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var q = db.Notifications.AsNoTracking().Where(n => n.TenantId == tenantId);
        if (status.HasValue) q = q.Where(n => n.Status == status.Value);
        return await q.CountAsync(ct).ConfigureAwait(false);
    }

    private static string TodayInRecipientTz(Recipient r)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(r.TimeZoneId);
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            return local.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    private static string HashRequest(SendNotificationRequest r, string payloadJson)
    {
        var sb = new StringBuilder();
        sb.Append(r.TemplateKey).Append('|').Append(r.Channel).Append('|').Append(r.RecipientExternalId).Append('|');
        sb.Append(r.Priority).Append('|').Append(r.Category).Append('|').Append(r.Locale).Append('|').Append(r.SendAt?.ToUnixTimeSeconds() ?? 0).Append('|').Append(payloadJson);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    internal static NotificationDto Map(Notification n) => new(
        n.Id, n.TenantId, n.RecipientId, n.TemplateKey, n.Channel, n.Category, n.Priority, n.Status,
        n.CreatedAt, n.ScheduledFor, n.DeliveredAt, n.FailedAt, n.AttemptCount, n.LastProvider, n.LastError, n.Address, n.CorrelationId);
}
