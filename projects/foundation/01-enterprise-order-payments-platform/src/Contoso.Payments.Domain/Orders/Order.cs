using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Events;

namespace Contoso.Payments.Domain.Orders;

/// <summary>
/// Order aggregate root.  Encapsulates the state machine
/// <c>Draft → Pending → AwaitingPayment → Paid → Fulfilled | Cancelled | Refunded | PartiallyRefunded</c>.
/// All transitions are validated; illegal transitions raise <see cref="DomainException"/>.
/// </summary>
public sealed class Order : AggregateRoot
{
    private readonly List<OrderLine> _lines = new();

    private Order() { CustomerRef = string.Empty; Currency = "USD"; RefundedTotal = new Money(0m, "USD"); }

    public Order(Guid id, string customerRef, string currency, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new DomainException("Order id required.");
        if (string.IsNullOrWhiteSpace(customerRef)) throw new DomainException("Customer ref required.");
        Id = id;
        CustomerRef = customerRef.Trim();
        Currency = currency.Trim().ToUpperInvariant();
        Status = OrderStatus.Draft;
        RefundedTotal = new Money(0m, Currency);
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public string CustomerRef { get; private set; }
    public string Currency { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public IReadOnlyList<OrderLine> Lines => _lines;
    public Money Total => _lines.Aggregate(Money.Zero(Currency), (a, l) => a.Add(l.LineTotal));

    /// <summary>Optional payment intent id set when the order enters AwaitingPayment.</summary>
    public Guid? PaymentIntentId { get; private set; }

    /// <summary>Cumulative refunded amount (in the order currency).</summary>
    public Money RefundedTotal { get; private set; }

    public void AddLine(Guid productId, string sku, int quantity, Money unitPrice)
    {
        EnsureStatus(OrderStatus.Draft);
        if (!string.Equals(unitPrice.Currency, Currency, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Line currency must match order currency.");
        // Copy the Money into a fresh instance — sharing the reference with a Product's Price causes
        // EF Core to track the same owned instance under two navigations and emit tracking warnings.
        var priceCopy = new Money(unitPrice.Amount, unitPrice.Currency);
        _lines.Add(new OrderLine(productId, sku, quantity, priceCopy));
        Version++;
    }

    /// <summary>
    /// Move Draft → Pending.  Inventory reservations must already be recorded onto each line
    /// via <see cref="AttachReservation"/> before this call — the API/handler enforces that.
    /// </summary>
    public void MarkPending(DateTimeOffset nowUtc)
    {
        EnsureStatus(OrderStatus.Draft);
        if (_lines.Count == 0)
            throw new DomainException("Cannot place an empty order.", "order.empty");
        Status = OrderStatus.Pending;
        Touch(nowUtc);
        RefundedTotal = Money.Zero(Currency);
        Raise(new OrderPlaced(Id, CustomerRef, Total.Amount, Currency, nowUtc));
    }

    public void AttachReservation(Guid orderLineId, Guid reservationId)
    {
        var line = _lines.FirstOrDefault(l => l.Id == orderLineId)
            ?? throw new DomainException("Order line not found.");
        line.ReservationId = reservationId;
    }

    public void MoveToAwaitingPayment(Guid paymentIntentId, DateTimeOffset nowUtc)
    {
        EnsureStatus(OrderStatus.Pending);
        PaymentIntentId = paymentIntentId;
        Status = OrderStatus.AwaitingPayment;
        Touch(nowUtc);
    }

    public void MarkPaid(DateTimeOffset nowUtc)
    {
        EnsureStatus(OrderStatus.AwaitingPayment);
        Status = OrderStatus.Paid;
        Touch(nowUtc);
    }

    public void MarkFulfilled(DateTimeOffset nowUtc)
    {
        EnsureStatus(OrderStatus.Paid);
        Status = OrderStatus.Fulfilled;
        Touch(nowUtc);
    }

    public void Cancel(DateTimeOffset nowUtc)
    {
        if (Status is OrderStatus.Fulfilled or OrderStatus.Refunded or OrderStatus.PartiallyRefunded)
            throw new DomainException($"Cannot cancel an order in status {Status}.", "order.illegal_transition");
        if (Status == OrderStatus.Cancelled) return; // idempotent
        Status = OrderStatus.Cancelled;
        Touch(nowUtc);
    }

    /// <summary>Called by refund handler after the ledger entry has been written.</summary>
    public void RecordRefund(Money refundedAmount, DateTimeOffset nowUtc)
    {
        if (Status is not (OrderStatus.Paid or OrderStatus.Fulfilled or OrderStatus.PartiallyRefunded))
            throw new DomainException($"Cannot refund an order in status {Status}.", "order.illegal_transition");
        if (!refundedAmount.IsPositive)
            throw new DomainException("Refund amount must be positive.");
        var newTotal = RefundedTotal.Add(refundedAmount);
        if (newTotal.GreaterThan(Total))
            throw new DomainException("Refund would exceed captured amount.", "refund.exceeds_captured");
        RefundedTotal = newTotal;
        Status = newTotal.Amount == Total.Amount
            ? OrderStatus.Refunded
            : OrderStatus.PartiallyRefunded;
        Touch(nowUtc);
    }

    private void EnsureStatus(OrderStatus expected)
    {
        if (Status != expected)
            throw new DomainException($"Illegal transition: order is {Status}, expected {expected}.", "order.illegal_transition");
    }

    private void Touch(DateTimeOffset nowUtc)
    {
        UpdatedAtUtc = nowUtc;
        Version++;
    }
}
