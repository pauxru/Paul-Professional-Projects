namespace NotificationPlatform.Infrastructure.Delivery;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Options;
using NotificationPlatform.Application.Receipts;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Domain.Notifications;
using NotificationPlatform.Infrastructure.Metrics;
using NotificationPlatform.Infrastructure.Persistence;

public sealed class ReceiptIngestor : IReceiptIngestor
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IWebhookSignatureService _sig;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly NotificationOptions _options;
    private readonly INotificationMetrics _metrics;
    private readonly ILogger<ReceiptIngestor> _log;

    public ReceiptIngestor(IDbContextFactory<AppDbContext> dbFactory, IWebhookSignatureService sig, IClock clock, IIdGenerator ids, IOptions<NotificationOptions> options, INotificationMetrics metrics, ILogger<ReceiptIngestor> log)
    {
        _dbFactory = dbFactory;
        _sig = sig;
        _clock = clock;
        _ids = ids;
        _options = options.Value;
        _metrics = metrics;
        _log = log;
    }

    public async Task<ReceiptIngestResult> IngestAsync(string signatureHeader, string timestampHeader, string nonceHeader, string body, CancellationToken ct)
    {
        var verification = _sig.Verify(signatureHeader, timestampHeader, body, _clock.UtcNow, TimeSpan.FromSeconds(_options.WebhookSignatureToleranceSeconds));
        if (!verification.IsValid) return new ReceiptIngestResult(false, verification.Reason);
        if (string.IsNullOrWhiteSpace(nonceHeader)) return new ReceiptIngestResult(false, "missing_nonce");

        ReceiptPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<ReceiptPayload>(body, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return new ReceiptIngestResult(false, "malformed_payload");
        }
        if (payload is null) return new ReceiptIngestResult(false, "empty_payload");
        if (payload.NotificationId == Guid.Empty) return new ReceiptIngestResult(false, "missing_notification_id");

        return await ApplyAsync(payload, nonceHeader, ct).ConfigureAwait(false);
    }

    public Task<ReceiptIngestResult> IngestTrustedAsync(ReceiptPayload payload, string nonce, CancellationToken ct)
        => ApplyAsync(payload, nonce, ct);

    private async Task<ReceiptIngestResult> ApplyAsync(ReceiptPayload payload, string nonce, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var replay = await db.Receipts.AnyAsync(r => r.Nonce == nonce, ct).ConfigureAwait(false);
        if (replay) return new ReceiptIngestResult(false, "replay_detected");

        var notification = await db.Notifications.SingleOrDefaultAsync(n => n.Id == payload.NotificationId, ct).ConfigureAwait(false);
        if (notification is null) return new ReceiptIngestResult(false, "notification_not_found");

        switch (payload.Kind.ToLowerInvariant())
        {
            case "delivered":
                notification.MarkDelivered(_clock.UtcNow);
                var tenant = await db.Tenants.AsNoTracking().SingleAsync(t => t.Id == notification.TenantId, ct).ConfigureAwait(false);
                _metrics.RecordDelivered(tenant.Slug, notification.Channel.ToString());
                break;
            case "bounced":
                notification.MarkBounced(payload.Reason ?? "bounced", _clock.UtcNow);
                if (!string.IsNullOrEmpty(notification.Address))
                {
                    var already = await db.Suppressions.AnyAsync(s => s.TenantId == notification.TenantId && s.Channel == notification.Channel && s.Address == NotificationPlatform.Domain.Suppressions.SuppressionEntry.NormalizeAddress(notification.Channel, notification.Address!), ct).ConfigureAwait(false);
                    if (!already)
                        db.Suppressions.Add(new NotificationPlatform.Domain.Suppressions.SuppressionEntry(_ids.NewId(), notification.TenantId, notification.Channel, notification.Address!, SuppressionReason.HardBounce, payload.Reason, _clock.UtcNow));
                }
                break;
            case "complained":
                notification.MarkBounced(payload.Reason ?? "complained", _clock.UtcNow);
                if (!string.IsNullOrEmpty(notification.Address))
                {
                    var already = await db.Suppressions.AnyAsync(s => s.TenantId == notification.TenantId && s.Channel == notification.Channel && s.Address == NotificationPlatform.Domain.Suppressions.SuppressionEntry.NormalizeAddress(notification.Channel, notification.Address!), ct).ConfigureAwait(false);
                    if (!already)
                        db.Suppressions.Add(new NotificationPlatform.Domain.Suppressions.SuppressionEntry(_ids.NewId(), notification.TenantId, notification.Channel, notification.Address!, SuppressionReason.Complaint, payload.Reason, _clock.UtcNow));
                }
                break;
            default:
                return new ReceiptIngestResult(false, $"unknown_kind:{payload.Kind}");
        }

        db.Receipts.Add(new DeliveryReceipt(_ids.NewId(), notification.TenantId, notification.Id, payload.ProviderName, payload.ProviderMessageId, payload.Kind, payload.Reason, nonce, _clock.UtcNow));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _log.LogInformation("Receipt applied to {NotificationId} kind={Kind}", notification.Id, payload.Kind);
        return new ReceiptIngestResult(true, null);
    }
}
