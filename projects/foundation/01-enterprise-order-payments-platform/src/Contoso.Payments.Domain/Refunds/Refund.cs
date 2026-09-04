using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Events;

namespace Contoso.Payments.Domain.Refunds;

public sealed class Refund : AggregateRoot
{
    private Refund() { Reason = string.Empty; Amount = new Money(0m, "USD"); }

    public Refund(Guid id, Guid orderId, Guid paymentIntentId, Money amount, string reason, DateTimeOffset issuedAtUtc)
    {
        if (id == Guid.Empty) throw new DomainException("Refund id required.");
        if (orderId == Guid.Empty) throw new DomainException("Order id required.");
        if (paymentIntentId == Guid.Empty) throw new DomainException("Payment intent id required.");
        if (!amount.IsPositive) throw new DomainException("Refund amount must be positive.", "refund.non_positive");
        Id = id;
        OrderId = orderId;
        PaymentIntentId = paymentIntentId;
        Amount = amount;
        Reason = string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
        IssuedAtUtc = issuedAtUtc;
        Version = 1;
        Raise(new RefundIssued(id, orderId, paymentIntentId, amount.Amount, amount.Currency, issuedAtUtc));
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid PaymentIntentId { get; private set; }
    public Money Amount { get; private set; }
    public string Reason { get; private set; }
    public DateTimeOffset IssuedAtUtc { get; private set; }
}
