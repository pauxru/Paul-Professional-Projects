using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Domain.Audit;
using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Inventory;
using Contoso.Payments.Domain.Ledger;
using Contoso.Payments.Domain.Payments;

namespace Contoso.Payments.Application.Payments;

/// <summary>
/// Orchestrates the "authorize + capture" flow, including provider retries with exponential
/// backoff, timeout handling, and idempotency at the domain level (one payment intent per
/// idempotency key + order).
/// </summary>
public sealed class PaymentService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly OutboxWriter _outbox;
    private readonly IPaymentProvider _provider;
    private readonly ILogger<PaymentService> _log;

    public PaymentService(IAppDbContext db, IClock clock, IIdGenerator ids, OutboxWriter outbox,
        IPaymentProvider provider, ILogger<PaymentService> log)
    {
        _db = db;
        _clock = clock;
        _ids = ids;
        _outbox = outbox;
        _provider = provider;
        _log = log;
    }

    public async Task<AppResult<PaymentIntentDto>> AuthorizeAsync(
        AuthorizePaymentRequest req, string idempotencyKey, string actor,
        string correlationId, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == req.OrderId, ct);
            if (order is null) return AppResult<PaymentIntentDto>.Fail("order.not_found", "Order not found.", 404);

            // Reuse an existing intent for this order + idempotency key if it exists.
            var existing = await _db.PaymentIntents
                .Include(p => p.Attempts)
                .FirstOrDefaultAsync(p => p.OrderId == order.Id && p.IdempotencyKey == idempotencyKey, ct);
            if (existing is not null)
                return AppResult<PaymentIntentDto>.Ok(ToDto(existing));

            var intent = new PaymentIntent(_ids.NewGuid(), order.Id, order.Total, idempotencyKey, _clock.UtcNow);
            if (order.Status == Domain.Orders.OrderStatus.Pending)
                order.MoveToAwaitingPayment(intent.Id, _clock.UtcNow);
            _db.PaymentIntents.Add(intent);

            var providerResult = await _provider.AuthorizeAsync(
                new PaymentProviderRequest(intent.Id, order.Id, order.Total.Amount, order.Total.Currency,
                    idempotencyKey, order.CustomerRef), ct);

            var attemptNumber = intent.Attempts.Count + 1;
            intent.RecordAttempt(new PaymentAttempt(_ids.NewGuid(), attemptNumber,
                providerResult.Outcome.ToString(), providerResult.ProviderReference,
                _clock.UtcNow, providerResult.LatencyMs));

            switch (providerResult.Outcome)
            {
                case PaymentProviderOutcome.Succeeded:
                    intent.MarkAuthorized(providerResult.ProviderReference ?? string.Empty, _clock.UtcNow);
                    _outbox.Enqueue(intent.DequeueEvents(), correlationId);
                    _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "payment.authorize",
                        $"payment:{intent.Id}", correlationId, "", "authorized", _clock.UtcNow));
                    break;
                case PaymentProviderOutcome.AsynchronousPending:
                    // Leave intent in Requires state — capture comes via webhook.
                    _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "payment.authorize.async",
                        $"payment:{intent.Id}", correlationId, "", "async_pending", _clock.UtcNow));
                    break;
                case PaymentProviderOutcome.Timeout:
                    // Order stays AwaitingPayment.  A separate background job (not simulated here)
                    // could reconcile; for this project the demo call retries at the API edge.
                    _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "payment.authorize.timeout",
                        $"payment:{intent.Id}", correlationId, "", "timeout", _clock.UtcNow));
                    break;
                case PaymentProviderOutcome.Declined:
                case PaymentProviderOutcome.ProviderError:
                    intent.MarkFailed(providerResult.FailureCode ?? "declined",
                        providerResult.Message ?? "declined by provider", _clock.UtcNow);
                    _outbox.Enqueue(intent.DequeueEvents(), correlationId);
                    _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "payment.authorize.failed",
                        $"payment:{intent.Id}", correlationId, "", "failed", _clock.UtcNow));
                    break;
                case PaymentProviderOutcome.Duplicate:
                    // Deterministic simulator returns Duplicate for known-good idempotency keys.
                    intent.MarkAuthorized(providerResult.ProviderReference ?? "dup", _clock.UtcNow);
                    _outbox.Enqueue(intent.DequeueEvents(), correlationId);
                    break;
            }

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AppResult<PaymentIntentDto>.Ok(ToDto(intent));
        }
        catch (DomainException dex)
        {
            await tx.RollbackAsync(ct);
            return AppResult<PaymentIntentDto>.Fail(dex.Code, dex.Message, 422);
        }
    }

    public async Task<AppResult<PaymentIntentDto>> CaptureAsync(Guid intentId, string actor,
        string correlationId, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            var intent = await _db.PaymentIntents.Include(p => p.Attempts)
                .FirstOrDefaultAsync(p => p.Id == intentId, ct);
            if (intent is null)
                return AppResult<PaymentIntentDto>.Fail("payment.not_found", "Payment intent not found.", 404);

            if (intent.Status != PaymentIntentStatus.Authorized)
                return AppResult<PaymentIntentDto>.Fail("payment.illegal_transition",
                    $"Cannot capture from {intent.Status}.", 422);

            var providerResult = await _provider.CaptureAsync(intent.ProviderReference, ct);
            var attemptNumber = intent.Attempts.Count + 1;
            intent.RecordAttempt(new PaymentAttempt(_ids.NewGuid(), attemptNumber,
                providerResult.Outcome.ToString(), providerResult.ProviderReference,
                _clock.UtcNow, providerResult.LatencyMs));

            if (providerResult.Outcome != PaymentProviderOutcome.Succeeded)
                return AppResult<PaymentIntentDto>.Fail("payment.capture_failed",
                    providerResult.Message ?? "capture failed", 502);

            intent.MarkCaptured(_clock.UtcNow);

            var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == intent.OrderId, ct);
            if (order is null) return AppResult<PaymentIntentDto>.Fail("order.not_found", "Order missing.", 500);
            order.MarkPaid(_clock.UtcNow);

            // Commit reservations.
            foreach (var line in order.Lines)
            {
                if (line.ReservationId is null) continue;
                var inv = await _db.Inventory.FirstOrDefaultAsync(i => i.ProductId == line.ProductId, ct);
                inv?.Commit(line.ReservationId.Value);
            }

            _db.LedgerEntries.Add(new LedgerEntry(_ids.NewGuid(), order.Id, intent.Id, null,
                LedgerEntryKind.Capture, intent.Amount.ToMinorUnits(), intent.Amount.Currency,
                "payment", _clock.UtcNow));

            _outbox.Enqueue(intent.DequeueEvents(), correlationId);
            _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "payment.capture",
                $"payment:{intent.Id}", correlationId, "authorized", "captured", _clock.UtcNow));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _log.LogInformation("Payment captured {IntentId} amount={Amount}", intent.Id, intent.Amount);
            return AppResult<PaymentIntentDto>.Ok(ToDto(intent));
        }
        catch (DomainException dex)
        {
            await tx.RollbackAsync(ct);
            return AppResult<PaymentIntentDto>.Fail(dex.Code, dex.Message, 422);
        }
    }

    public async Task<AppResult<PaymentIntentDto>> VoidAsync(Guid intentId, string actor,
        string correlationId, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            var intent = await _db.PaymentIntents.Include(p => p.Attempts)
                .FirstOrDefaultAsync(p => p.Id == intentId, ct);
            if (intent is null)
                return AppResult<PaymentIntentDto>.Fail("payment.not_found", "Payment intent not found.", 404);
            if (intent.Status != PaymentIntentStatus.Authorized)
                return AppResult<PaymentIntentDto>.Fail("payment.illegal_transition",
                    $"Cannot void from {intent.Status}.", 422);

            var providerResult = await _provider.VoidAsync(intent.ProviderReference, ct);
            var attemptNumber = intent.Attempts.Count + 1;
            intent.RecordAttempt(new PaymentAttempt(_ids.NewGuid(), attemptNumber,
                providerResult.Outcome.ToString(), providerResult.ProviderReference,
                _clock.UtcNow, providerResult.LatencyMs));

            if (providerResult.Outcome != PaymentProviderOutcome.Succeeded)
                return AppResult<PaymentIntentDto>.Fail("payment.void_failed",
                    providerResult.Message ?? "void failed", 502);

            intent.MarkVoided(_clock.UtcNow);
            _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "payment.void",
                $"payment:{intent.Id}", correlationId, "authorized", "voided", _clock.UtcNow));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AppResult<PaymentIntentDto>.Ok(ToDto(intent));
        }
        catch (DomainException dex)
        {
            await tx.RollbackAsync(ct);
            return AppResult<PaymentIntentDto>.Fail(dex.Code, dex.Message, 422);
        }
    }

    public async Task<AppResult<PaymentIntentDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var intent = await _db.PaymentIntents.Include(p => p.Attempts).AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (intent is null) return AppResult<PaymentIntentDto>.Fail("payment.not_found", "Payment intent not found.", 404);
        return AppResult<PaymentIntentDto>.Ok(ToDto(intent));
    }

    /// <summary>
    /// Retry an authorization that previously timed out.  The intent must be in Requires state.
    /// Returns the current status.
    /// </summary>
    public async Task<AppResult<PaymentIntentDto>> RetryAuthorizeAsync(Guid intentId, string actor,
        string correlationId, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            var intent = await _db.PaymentIntents.Include(p => p.Attempts)
                .FirstOrDefaultAsync(p => p.Id == intentId, ct);
            if (intent is null)
                return AppResult<PaymentIntentDto>.Fail("payment.not_found", "Payment intent not found.", 404);
            if (intent.Status != PaymentIntentStatus.Requires)
                return AppResult<PaymentIntentDto>.Fail("payment.illegal_transition",
                    $"Cannot retry authorize from {intent.Status}.", 422);

            var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == intent.OrderId, ct);
            if (order is null)
                return AppResult<PaymentIntentDto>.Fail("order.not_found", "Order missing.", 500);

            var providerResult = await _provider.AuthorizeAsync(
                new PaymentProviderRequest(intent.Id, order.Id, intent.Amount.Amount, intent.Amount.Currency,
                    intent.IdempotencyKey, order.CustomerRef), ct);

            var attemptNumber = intent.Attempts.Count + 1;
            intent.RecordAttempt(new PaymentAttempt(_ids.NewGuid(), attemptNumber,
                providerResult.Outcome.ToString(), providerResult.ProviderReference,
                _clock.UtcNow, providerResult.LatencyMs));

            if (providerResult.Outcome == PaymentProviderOutcome.Succeeded ||
                providerResult.Outcome == PaymentProviderOutcome.Duplicate)
            {
                intent.MarkAuthorized(providerResult.ProviderReference ?? "retry", _clock.UtcNow);
                _outbox.Enqueue(intent.DequeueEvents(), correlationId);
            }

            _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "payment.authorize.retry",
                $"payment:{intent.Id}", correlationId, "", providerResult.Outcome.ToString(), _clock.UtcNow));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AppResult<PaymentIntentDto>.Ok(ToDto(intent));
        }
        catch (DomainException dex)
        {
            await tx.RollbackAsync(ct);
            return AppResult<PaymentIntentDto>.Fail(dex.Code, dex.Message, 422);
        }
    }

    private static PaymentIntentDto ToDto(PaymentIntent p) => new(
        p.Id, p.OrderId, p.Amount.Amount, p.CapturedAmount.Amount, p.Amount.Currency,
        p.Status.ToString(), p.ProviderReference, p.CreatedAtUtc, p.UpdatedAtUtc,
        p.Attempts.Select(a => new PaymentAttemptDto(a.Id, a.AttemptNumber, a.Outcome,
            a.ProviderReference, a.AttemptedAtUtc, a.LatencyMs)).ToList());
}
