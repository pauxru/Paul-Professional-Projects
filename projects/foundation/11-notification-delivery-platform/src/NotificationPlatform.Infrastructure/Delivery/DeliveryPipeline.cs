namespace NotificationPlatform.Infrastructure.Delivery;

using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Fairness;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Application.Options;
using NotificationPlatform.Application.Providers;
using NotificationPlatform.Application.Receipts;
using NotificationPlatform.Application.Templates;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Domain.Notifications;
using NotificationPlatform.Domain.Recipients;
using NotificationPlatform.Infrastructure.Metrics;
using NotificationPlatform.Infrastructure.Persistence;
using NotificationPlatform.Infrastructure.Providers.Common;
using NotificationPlatform.Infrastructure.Templates;

public sealed class DeliveryPipeline : IDeliveryPipeline
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly IProviderRegistry _providers;
    private readonly IProviderHealthTracker _health;
    private readonly IProviderRateLimiter _limiter;
    private readonly ITemplateEngine _engine;
    private readonly TemplateService _templateService;
    private readonly ITenantFairnessScheduler _fairness;
    private readonly IBackoffPolicy _backoff;
    private readonly IReceiptIngestor _receipts;
    private readonly INotificationMetrics _metrics;
    private readonly NotificationOptions _options;
    private readonly ILogger<DeliveryPipeline> _log;

    public DeliveryPipeline(
        IDbContextFactory<AppDbContext> dbFactory,
        IClock clock,
        IIdGenerator ids,
        IProviderRegistry providers,
        IProviderHealthTracker health,
        IProviderRateLimiter limiter,
        ITemplateEngine engine,
        TemplateService templateService,
        ITenantFairnessScheduler fairness,
        IBackoffPolicy backoff,
        IReceiptIngestor receipts,
        INotificationMetrics metrics,
        IOptions<NotificationOptions> options,
        ILogger<DeliveryPipeline> log)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _ids = ids;
        _providers = providers;
        _health = health;
        _limiter = limiter;
        _engine = engine;
        _templateService = templateService;
        _fairness = fairness;
        _backoff = backoff;
        _receipts = receipts;
        _metrics = metrics;
        _options = options.Value;
        _log = log;
    }

    public async Task<int> ScanScheduledAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var due = await db.Notifications
            .Where(n => n.Status == NotificationStatus.Scheduled && n.ScheduledFor <= now)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        foreach (var n in due) n.MarkQueued();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return due.Count;
    }

    public async Task<int> ProcessDueAsync(int batchSize, CancellationToken ct)
    {
        await ScanScheduledAsync(ct).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var pending = await db.Notifications.AsNoTracking()
            .Where(n => n.Status == NotificationStatus.Queued && (n.ScheduledFor == null || n.ScheduledFor <= now))
            .OrderBy(n => n.CreatedAt)
            .Take(batchSize * 20)
            .Select(n => new { n.Id, n.TenantId })
            .ToListAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0) return 0;

        var tenants = pending.Select(p => p.TenantId).Distinct().ToList();
        var tenantWeights = await db.Tenants.AsNoTracking().Where(t => tenants.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.FairnessWeight, ct).ConfigureAwait(false);
        var backlogs = pending.GroupBy(p => p.TenantId)
            .Select(g => new TenantBacklog(g.Key, tenantWeights.GetValueOrDefault(g.Key, 1.0), g.Select(x => x.Id).ToList()))
            .ToList();
        var picked = _fairness.PickBatch(backlogs, batchSize);
        int processed = 0;
        foreach (var id in picked)
        {
            if (await ProcessOneAsync(id, ct).ConfigureAwait(false)) processed++;
        }
        return processed;
    }

    public async Task<int> ProcessTenantAsync(Guid tenantId, int batchSize, CancellationToken ct)
    {
        await ScanScheduledAsync(ct).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var now = _clock.UtcNow;
        var ids = await db.Notifications.AsNoTracking()
            .Where(n => n.TenantId == tenantId
                        && n.Status == NotificationStatus.Queued
                        && (n.ScheduledFor == null || n.ScheduledFor <= now))
            .OrderBy(n => n.CreatedAt)
            .Take(batchSize)
            .Select(n => n.Id)
            .ToListAsync(ct).ConfigureAwait(false);
        int processed = 0;
        foreach (var id in ids)
        {
            if (await ProcessOneAsync(id, ct).ConfigureAwait(false)) processed++;
        }
        return processed;
    }

    private async Task<bool> ProcessOneAsync(Guid notificationId, CancellationToken ct)
    {
        using var span = _metrics.ActivitySource.StartActivity("notification.process", ActivityKind.Internal);
        span?.SetTag("notification.id", notificationId);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var n = await db.Notifications.SingleOrDefaultAsync(x => x.Id == notificationId, ct).ConfigureAwait(false);
        if (n is null) return false;
        if (n.Status != NotificationStatus.Queued && n.Status != NotificationStatus.Scheduled) return false;

        var tenant = await db.Tenants.SingleAsync(t => t.Id == n.TenantId, ct).ConfigureAwait(false);
        var recipient = await db.Recipients.SingleAsync(r => r.Id == n.RecipientId, ct).ConfigureAwait(false);

        // Rendering stage
        n.BeginRendering();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var renderStopwatch = Stopwatch.StartNew();
        string? subject;
        string body;
        int templateVersion;
        try
        {
            var template = await _templateService.ResolveWithFallbackAsync(n.TenantId, n.TemplateKey, n.Channel, n.Locale, tenant.DefaultLocale, ct).ConfigureAwait(false);
            if (template is null)
            {
                n.MarkFailed("template_not_found", _clock.UtcNow);
                _metrics.RecordFailure(tenant.Slug, n.Channel.ToString(), "-", "template_not_found");
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return true;
            }
            var doc = JsonDocument.Parse(n.PayloadJson);
            using (doc)
            {
                var bodyResult = _engine.Render(template.Body, doc.RootElement, template.StrictMode);
                body = bodyResult.Body;
                subject = string.IsNullOrEmpty(template.Subject) ? null : _engine.Render(template.Subject!, doc.RootElement, template.StrictMode).Body;
                templateVersion = template.Version;
            }
        }
        catch (TemplateRenderException ex)
        {
            n.MarkFailed("render:" + ex.Message, _clock.UtcNow);
            _metrics.RecordFailure(tenant.Slug, n.Channel.ToString(), "-", "render_error");
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }
        finally
        {
            renderStopwatch.Stop();
            _metrics.RecordRenderDuration(renderStopwatch.Elapsed.TotalMilliseconds);
        }

        var address = recipient.AddressFor(n.Channel);
        if (string.IsNullOrEmpty(address))
        {
            n.MarkFailed("no_address", _clock.UtcNow);
            _metrics.RecordFailure(tenant.Slug, n.Channel.ToString(), "-", "no_address");
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }

        n.CompleteRendering(templateVersion, subject, body, address);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Frequency counter for marketing
        if (n.Category == NotificationCategory.Marketing)
        {
            var dateKey = TodayInTz(recipient);
            var counter = await db.FrequencyCounters.SingleOrDefaultAsync(c => c.TenantId == n.TenantId && c.RecipientId == n.RecipientId && c.Category == n.Category && c.DateKey == dateKey, ct).ConfigureAwait(false);
            if (counter is null) db.FrequencyCounters.Add(new NotificationPlatform.Domain.Providers.RecipientFrequencyCounter(n.TenantId, n.RecipientId, n.Category, dateKey, 1));
            else counter.Increment();
        }

        // Provider selection: iterate providers with health filter
        var providers = _providers.ProvidersFor(n.Channel);
        if (providers.Count == 0)
        {
            n.MarkFailed("no_provider", _clock.UtcNow);
            _metrics.RecordFailure(tenant.Slug, n.Channel.ToString(), "-", "no_provider");
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return true;
        }

        ProviderSendResult? successResult = null;
        IChannelProvider? successProvider = null;
        TimeSpan? throttleRetryAfter = null;
        bool transient = false;
        string? lastError = null;

        foreach (var provider in providers)
        {
            var now = _clock.UtcNow;
            var canSend = await _health.ProbeAndMaybeHalfOpenAsync(provider.Name, now, ct).ConfigureAwait(false);
            if (!canSend)
            {
                lastError = $"{provider.Name}:circuit_open";
                transient = true; // circuit will close later; treat as retryable
                continue;
            }
            var acquired = _limiter.TryAcquire(provider.Name);
            if (!acquired)
            {
                lastError = $"{provider.Name}:rate_limited";
                throttleRetryAfter ??= TimeSpan.FromMilliseconds(500);
                transient = true; // rate limits are retryable
                continue;
            }

            var sendSw = Stopwatch.StartNew();
            var req = new ProviderSendRequest(n.Id, n.TenantId, address, subject, body, n.Category.ToString(), n.CorrelationId ?? string.Empty, n.AttemptCount + 1);
            var result = await provider.SendAsync(req, ct).ConfigureAwait(false);
            sendSw.Stop();
            _metrics.RecordSendDuration(sendSw.Elapsed.TotalMilliseconds, provider.Name, n.Channel.ToString());

            var attempt = new NotificationAttempt(_ids.NewId(), n.Id, n.AttemptCount + 1, provider.Name, result.Kind, result.Error, result.LatencyMilliseconds, _clock.UtcNow, result.ProviderMessageId);
            n.RecordAttempt(attempt);
            db.Attempts.Add(attempt);

            if (result.Kind == ProviderResultKind.Success)
            {
                await _health.RecordSuccessAsync(provider.Name, provider.Channel, ct).ConfigureAwait(false);
                successResult = result;
                successProvider = provider;
                break;
            }

            if (result.Kind == ProviderResultKind.PermanentFailure)
            {
                await _health.RecordFailureAsync(provider.Name, provider.Channel, _clock.UtcNow, ct).ConfigureAwait(false);
                lastError = result.Error ?? "permanent";
                transient = false;
                continue;
            }

            // Transient or throttled -> failover to next provider
            await _health.RecordFailureAsync(provider.Name, provider.Channel, _clock.UtcNow, ct).ConfigureAwait(false);
            lastError = result.Error ?? result.Kind.ToString();
            transient = true;
            if (result.Kind == ProviderResultKind.Throttled)
                throttleRetryAfter = result.RetryAfter;
        }

        if (successResult is not null)
        {
            n.MarkSent(_clock.UtcNow);
            _metrics.RecordDispatched(tenant.Slug, n.Channel.ToString(), successProvider!.Name);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            // Simulate delivery receipt back to the platform via trusted internal path.
            await _receipts.IngestTrustedAsync(new ReceiptPayload(n.Id, successProvider.Name, successResult.ProviderMessageId ?? _ids.NewCorrelationId(), "delivered", null), _ids.NewCorrelationId(), ct).ConfigureAwait(false);
            return true;
        }

        // No provider succeeded — decide retry vs DLQ
        if (!transient || !n.HasMoreAttempts())
        {
            if (transient)
            {
                n.MarkDeadLettered(lastError ?? "max_attempts_exceeded", _clock.UtcNow);
                _metrics.RecordDeadLettered(tenant.Slug, n.Channel.ToString());
            }
            else
            {
                n.MarkFailed(lastError ?? "permanent", _clock.UtcNow);
                _metrics.RecordFailure(tenant.Slug, n.Channel.ToString(), n.LastProvider ?? "-", "permanent");
            }
        }
        else
        {
            var delay = throttleRetryAfter ?? _backoff.NextDelay(n.AttemptCount);
            n.MarkScheduled(_clock.UtcNow + delay);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    private static string TodayInTz(Recipient recipient)
    {
        try
        {
            var tz = TimeZoneInfo.FindSystemTimeZoneById(recipient.TimeZoneId);
            var local = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz);
            return local.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
        catch
        {
            return DateTimeOffset.UtcNow.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
