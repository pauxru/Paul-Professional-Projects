using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Domain.Audit;
using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Inventory;
using Contoso.Payments.Domain.Orders;

namespace Contoso.Payments.Application.Orders;

public sealed class OrderService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly OutboxWriter _outbox;
    private readonly ILogger<OrderService> _log;

    public OrderService(IAppDbContext db, IClock clock, IIdGenerator ids,
        OutboxWriter outbox, ILogger<OrderService> log)
    {
        _db = db;
        _clock = clock;
        _ids = ids;
        _outbox = outbox;
        _log = log;
    }

    public async Task<AppResult<OrderDto>> PlaceOrderAsync(PlaceOrderRequest req, string actor, string correlationId, CancellationToken ct)
    {
        if (req.Lines is null || req.Lines.Count == 0)
            return AppResult<OrderDto>.Fail("order.empty", "Order requires at least one line.", 422);

        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            var order = new Order(_ids.NewGuid(), req.CustomerRef, req.Currency, _clock.UtcNow);

            foreach (var line in req.Lines)
            {
                var normalizedSku = line.Sku.Trim().ToUpperInvariant();
                var product = await _db.Products.FirstOrDefaultAsync(p => p.Sku == normalizedSku, ct);
                if (product is null)
                    return AppResult<OrderDto>.Fail("catalog.sku_not_found", $"Unknown SKU {line.Sku}.", 422);
                if (!product.IsActive)
                    return AppResult<OrderDto>.Fail("catalog.inactive", $"SKU {line.Sku} is inactive.", 422);
                if (!string.Equals(product.Price.Currency, req.Currency, StringComparison.OrdinalIgnoreCase))
                    return AppResult<OrderDto>.Fail("catalog.currency_mismatch", $"SKU {line.Sku} priced in {product.Price.Currency}.", 422);

                order.AddLine(product.Id, product.Sku, line.Quantity, product.Price);
            }

            // Reserve stock line-by-line under the same transaction.  Concurrency safety is
            // enforced by SaveChanges' optimistic concurrency token on InventoryItem.Version.
            foreach (var l in order.Lines)
            {
                var inv = await _db.Inventory.FirstOrDefaultAsync(i => i.ProductId == l.ProductId, ct);
                if (inv is null)
                    return AppResult<OrderDto>.Fail("inventory.not_tracked", $"Inventory not tracked for {l.Sku}.", 422);
                var reservationId = inv.Reserve(order.Id, l.Quantity);
                order.AttachReservation(l.Id, reservationId);
            }

            order.MarkPending(_clock.UtcNow);

            _db.Orders.Add(order);

            var events = order.DequeueEvents();
            _outbox.Enqueue(events, correlationId);
            _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "order.place",
                $"order:{order.Id}", correlationId,
                beforeHash: "", afterHash: Hashing.Sha256Hex(order.Id.ToString()),
                _clock.UtcNow));

            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            _log.LogInformation("Order placed {OrderId} lines={Lines} total={Total} currency={Currency}",
                order.Id, order.Lines.Count, order.Total.Amount, order.Total.Currency);

            return AppResult<OrderDto>.Ok(ToDto(order));
        }
        catch (DomainException dex)
        {
            await tx.RollbackAsync(ct);
            return AppResult<OrderDto>.Fail(dex.Code, dex.Message, 422);
        }
        catch (DbUpdateConcurrencyException)
        {
            await tx.RollbackAsync(ct);
            return AppResult<OrderDto>.Fail("inventory.concurrent_modification",
                "Inventory changed under a competing request.  Retry.", 409);
        }
        catch (DbUpdateException dbex)
        {
            await tx.RollbackAsync(ct);
            _log.LogWarning(dbex, "Order placement DB conflict");
            return AppResult<OrderDto>.Fail("inventory.database_conflict",
                "Database conflict placing order.  Retry.", 409);
        }
    }

    public async Task<AppResult<OrderDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var order = await _db.Orders.Include(o => o.Lines).AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, ct);
        if (order is null) return AppResult<OrderDto>.Fail("order.not_found", "Order not found.", 404);
        return AppResult<OrderDto>.Ok(ToDto(order));
    }

    public async Task<AppResult<OrderDto>> CancelAsync(Guid id, string actor, string correlationId, CancellationToken ct)
    {
        await using var tx = await _db.BeginTransactionAsync(ct);
        try
        {
            var order = await _db.Orders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct);
            if (order is null) return AppResult<OrderDto>.Fail("order.not_found", "Order not found.", 404);
            order.Cancel(_clock.UtcNow);

            // Release reservations.
            foreach (var line in order.Lines)
            {
                if (line.ReservationId is null) continue;
                var inv = await _db.Inventory.FirstOrDefaultAsync(i => i.ProductId == line.ProductId, ct);
                inv?.Release(line.ReservationId.Value);
            }

            _db.AuditEvents.Add(new AuditEvent(_ids.NewGuid(), actor, "order.cancel",
                $"order:{order.Id}", correlationId, "", "", _clock.UtcNow));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return AppResult<OrderDto>.Ok(ToDto(order));
        }
        catch (DomainException dex)
        {
            await tx.RollbackAsync(ct);
            return AppResult<OrderDto>.Fail(dex.Code, dex.Message, 422);
        }
    }

    public async Task<PagedResult<OrderDto>> ListAsync(int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var q = _db.Orders.Include(o => o.Lines).AsNoTracking().OrderByDescending(o => o.CreatedAtUtc);
        var total = await q.CountAsync(ct);
        var items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<OrderDto>(items.Select(ToDto).ToList(), page, pageSize, total);
    }

    private static OrderDto ToDto(Order o) => new(
        o.Id, o.CustomerRef, o.Currency, o.Status.ToString(), o.Total.Amount,
        o.RefundedTotal.Amount, o.PaymentIntentId,
        o.Lines.Select(l => new OrderLineDto(l.Id, l.Sku, l.Quantity, l.UnitPrice.Amount)).ToList(),
        o.CreatedAtUtc, o.UpdatedAtUtc, o.Version);
}
