namespace Contoso.Payments.Domain.Orders;

/// <summary>
/// Explicit state machine for the order aggregate.  Every transition is validated by
/// <see cref="Order"/> — illegal transitions raise a domain exception.  The full transition
/// matrix is pinned by a unit test.
/// </summary>
public enum OrderStatus
{
    Draft = 0,
    Pending = 1,
    AwaitingPayment = 2,
    Paid = 3,
    Fulfilled = 4,
    Cancelled = 5,
    Refunded = 6,
    PartiallyRefunded = 7
}
