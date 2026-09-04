using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Domain.Audit;
using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Ledger;
using Contoso.Payments.Domain.Orders;
using Contoso.Payments.Domain.Payments;

namespace Contoso.Payments.Application.Webhooks;

/// <summary>
/// Handles inbound provider webhooks: verifies HMAC, guards against replay, then advances the
/// payment intent state machine and issues audit/ledger entries.
/// </summary>
public sealed class WebhookService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly OutboxWriter _outbox;
    private readonly IWebhookSignatureVerifier _verifier;
    private readonly IWebhookReplayStore _replay;
    private readonly ILogger<WebhookService> _log;

    public WebhookService(IAppDbContext db, IClock clock, IIdGenerator ids, OutboxWriter outbox,
        IWebhookSignatureVerifier verifier, IWebhookReplayStore replay, ILogger<WebhookService> log)
    {
        _db = db;
        _clock = clock;
        _ids = ids;
        _outbox = outbox;
        _verifier = verifier;
        _replay = replay;
        _log = log;
    }

    public async Task<WebhookProcessingResult> ProcessAsync(
        string signatureHeader, byte[] rawBody, string correlationId, CancellationToken ct)
    {
        if (!_verifier.Verify(signatureHeader, rawBody, _clock.UtcNow, out var failureReason))
        {
            _log.LogWarning("Webhook signature invalid: {Reason}", failureReason);
            return new WebhookProcessingResult(false, 401, failureReason ?? "signature invalid", false);
        }

        var signatureHash = Hashing.Sha256Hex(signatureHeader);
        if (await _replay.SeenAsync(signatureHash, ct))
        {
            _log.LogInformation("Webhook replay ignored {Sig}", signatureHash);
            return new WebhookProcessingResult(true, 200, "replay ignored", true);
        }

        WebhookPayload? payload;
        try
        {
            payload = JsonSerializer.Deserialize<WebhookPayload>(rawBody, JsonOptions);
        }
        catch (JsonException)
        {
            return new WebhookProcessingResult(false, 400, "malformed body", false);
        }
        if (payload is null)
            return new WebhookProcessingResult(false, 400, "empty body", false);

        await using var tx = await _db.BeginTransactionAsync(ct);
        var intent = await _db.PaymentIntents.Include(p => p.Attempts)
            .FirstOrDefaultAsync(p => p.Id == payload.PaymentIntentId, ct);
        if (intent is null)
        {
            await tx.RollbackAsync(ct);
            return new WebhookProcessingResult(false, 404, "intent not found", false);
        }

        try
        {
            switch (payload.EventType)
            {
                case "payment.authorized":
                    if (intent.Status == PaymentIntentStatus.Requires)
                    {
                        intent.MarkAuthorized(payload.ProviderReference, _clock.UtcNow);
                        _outbox.Enqueue(intent.DequeueEvents(), correlationId);
                    }
                    break;
                case "payment.captured":
                    if (intent.Status == PaymentIntentStatus.Requires)
                        intent.MarkAuthorized(payload.ProviderReference, _clock.UtcNow);
                    if (intent.Status == PaymentIntentStatus.Authorized)
                    {
                        intent.MarkCaptured(_clock.UtcNow);
                        var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == intent.OrderId, ct);
                        if (order is not null)
                        {
                            if (order.Status == OrderStatus.AwaitingPayment)
                                order.MarkPaid(_clock.UtcNow);
                            foreach (var line in order.Lines)
                            {
                                if (line.ReservationId is null) continue;
                                var inv = await _db.Inventory.FirstOrDefaultAsync(i => i.ProductId == line.ProductId, ct);
                                inv?.Commit(line.ReservationId.Value);
                            }
                        }
                        _db.LedgerEntries.Add(new LedgerEntry(_ids.NewGuid(), intent.OrderId, intent.Id, null,
                            LedgerEntryKind.Capture, intent.Amount.ToMinorUnits(), intent.Amount.Currency,
                            "webhook", _clock.UtcNow));
                        _outbox.Enqueue(intent.DequeueEvents(), correlationId);
                    }
                    break;
                case "payment.failed":
                    if (intent.Status is PaymentIntentStatus.Requires or PaymentIntentStatus.Authorized)
                    {
                        intent.MarkFailed(payload.FailureCode ?? "provider_failed",
                            payload.FailureMessage ?? "provider reported failure", _clock.UtcNow);
                        _outbox.Enqueue(intent.DequeueEvents(), correlationId);
                    }
                    break;
                default:
                    // Unknown event type — ignore but record it and mark seen.
                    _log.LogInformation("Webhook unknown event type {Type} ignored", payload.EventType);
                    break;
            }

            _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), "webhook", $"webhook.{payload.EventType}",
                $"payment:{intent.Id}", correlationId, "", intent.Status.ToString(), _clock.UtcNow));

            await _replay.RecordAsync(signatureHash, _clock.UtcNow, ct);
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new WebhookProcessingResult(true, 200, "accepted", false);
        }
        catch (DomainException dex)
        {
            await tx.RollbackAsync(ct);
            _log.LogWarning(dex, "Webhook rejected by domain rule");
            return new WebhookProcessingResult(false, 422, dex.Message, false);
        }
    }
}

public sealed record WebhookProcessingResult(bool Success, int HttpStatus, string Detail, bool Replay);
