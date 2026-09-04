using Contoso.Payments.Domain.Common;

namespace Contoso.Payments.Domain.Orders;

public sealed class OrderLine
{
    private OrderLine() { Sku = string.Empty; UnitPrice = new Money(0m, "USD"); }

    public OrderLine(Guid productId, string sku, int quantity, Money unitPrice)
    {
        if (productId == Guid.Empty) throw new DomainException("Line productId required.");
        if (quantity <= 0) throw new DomainException("Line quantity must be positive.");
        if (!unitPrice.IsPositive) throw new DomainException("Line unit price must be positive.");
        ProductId = productId;
        Sku = sku.Trim().ToUpperInvariant();
        Quantity = quantity;
        UnitPrice = unitPrice;
    }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid ProductId { get; private set; }
    public string Sku { get; private set; }
    public int Quantity { get; private set; }
    public Money UnitPrice { get; private set; }
    public Money LineTotal => UnitPrice.Multiply(Quantity);

    /// <summary>Reservation id issued by inventory, if any. Present once the order is Pending.</summary>
    public Guid? ReservationId { get; set; }
}
