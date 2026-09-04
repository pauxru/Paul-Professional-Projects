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
using Contoso.Payments.Domain.Refunds;

namespace Contoso.Payments.Application.Refunds;

public sealed class RefundService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly OutboxWriter _outbox;
    private readonly IPaymentProvider _provider;
    private readonly ILogger<RefundService> _log;

    public RefundService(IAppDbContext db, IClock clock, IIdGenerator ids, OutboxWriter outbox,
        IPaymentProvider provider, ILogger<RefundService> log)
    {
        _db = db;
        _clock = clock;
        _ids = ids;
        _outbox = outbox;
        _provider = provider;
        _log = log;
    }

    public async Task<AppResult<RefundDto>> IssueAsync(IssueRefundRequest req, string actor,
        string correlationId, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == req.OrderId, ct);
            if (order is null) return AppResult<RefundDto>.Fail("order.not_found", "Order not found.", 404);

            var intent = await _db.PaymentIntents
                .FirstOrDefaultAsync(p => p.OrderId == order.Id && p.Status == PaymentIntentStatus.Captured, ct);
            if (intent is null)
                return AppResult<RefundDto>.Fail("refund.no_capture", "No captured payment to refund.", 422);

            var refundAmount = Money.Of(req.Amount, order.Currency);
            // Rely on the domain to check the "does not exceed captured" invariant.
            var refund = new Refund(_ids.NewGuid(), order.Id, intent.Id, refundAmount, req.Reason, _clock.UtcNow);
            order.RecordRefund(refundAmount, _clock.UtcNow);

            var providerResult = await _provider.RefundAsync(intent.ProviderReference,
                refundAmount.Amount, refundAmount.Currency, ct);
            if (providerResult.Outcome != PaymentProviderOutcome.Succeeded)
                return AppResult<RefundDto>.Fail("refund.provider_failed",
                    providerResult.Message ?? "provider refused refund", 502);

            _db.Refunds.Add(refund);
            _db.LedgerEntries.Add(new LedgerEntry(_ids.NewGuid(), order.Id, intent.Id, refund.Id,
                LedgerEntryKind.Refund, -refundAmount.ToMinorUnits(), refundAmount.Currency,
                "refund", _clock.UtcNow));

            _outbox.Enqueue(refund.DequeueEvents(), correlationId);
            _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "refund.issue",
                $"refund:{refund.Id}", correlationId, "", "issued", _clock.UtcNow));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _log.LogInformation("Refund issued {RefundId} order={OrderId} amount={Amount}",
                refund.Id, order.Id, refund.Amount);
            return AppResult<RefundDto>.Ok(new RefundDto(refund.Id, refund.OrderId,
                refund.PaymentIntentId, refund.Amount.Amount, refund.Amount.Currency,
                refund.Reason, refund.IssuedAtUtc));
        }
        catch (DomainException dex)
        {
            await tx.RollbackAsync(ct);
            var status = dex.Code == "refund.exceeds_captured" ? 422 : 422;
            return AppResult<RefundDto>.Fail(dex.Code, dex.Message, status);
        }
    }

    public async Task<PagedResult<RefundDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = _db.Refunds.AsNoTracking().OrderByDescending(r => r.IssuedAtUtc);
        var total = await q.CountAsync(ct);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<RefundDto>(items.Select(r => new RefundDto(r.Id, r.OrderId,
            r.PaymentIntentId, r.Amount.Amount, r.Amount.Currency, r.Reason, r.IssuedAtUtc)).ToList(),
            page, pageSize, total);
    }
}
