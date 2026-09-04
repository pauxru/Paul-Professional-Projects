namespace Contoso.Storefront.Domain;

public enum OrderStatus
{
    Draft = 0,
    Submitted = 1,
    Fulfilled = 2,
    Cancelled = 3
}

public sealed class Order
{
    private readonly List<OrderItem> _items = [];

    private Order()
    {
    }

    public Order(Guid id, string customerReference, string idempotencyKey, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Order id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(customerReference))
        {
            throw new ArgumentException("Customer reference is required.", nameof(customerReference));
        }

        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            throw new ArgumentException("Idempotency key is required.", nameof(idempotencyKey));
        }

        Id = id;
        CustomerReference = customerReference.Trim();
        IdempotencyKey = idempotencyKey.Trim();
        CreatedAt = createdAt;
        Status = OrderStatus.Draft;
    }

    public Guid Id { get; private set; }
    public string CustomerReference { get; private set; } = string.Empty;
    public string IdempotencyKey { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public OrderStatus Status { get; private set; }
    public int Version { get; private set; }
    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();

    public Money Total
    {
        get
        {
            if (_items.Count == 0)
            {
                return new Money(0, "USD");
            }

            return _items
                .Select(item => item.LineTotal)
                .Aggregate((left, right) => left + right);
        }
    }

    public void AddItem(Guid productId, string productName, int quantity, Money unitPrice)
    {
        if (Status != OrderStatus.Draft)
        {
            throw new InvalidOperationException("Items cannot be changed after submission.");
        }

        if (productId == Guid.Empty)
        {
            throw new ArgumentException("Product id is required.", nameof(productId));
        }

        if (quantity is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be between 1 and 100.");
        }

        if (_items.Count > 0 && !_items.All(item => item.Currency == unitPrice.Currency))
        {
            throw new InvalidOperationException("All order items must use the same currency.");
        }

        _items.Add(new OrderItem(Guid.NewGuid(), Id, productId, productName, quantity, unitPrice));
    }

    public void Submit()
    {
        if (_items.Count == 0)
        {
            throw new InvalidOperationException("An order requires at least one item.");
        }

        Status = OrderStatus.Submitted;
        Version++;
    }
}

public sealed class OrderItem
{
    private OrderItem()
    {
    }

    internal OrderItem(
        Guid id,
        Guid orderId,
        Guid productId,
        string productName,
        int quantity,
        Money unitPrice)
    {
        Id = id;
        OrderId = orderId;
        ProductId = productId;
        ProductName = productName;
        Quantity = quantity;
        UnitPriceAmount = unitPrice.Amount;
        Currency = unitPrice.Currency;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid ProductId { get; private set; }
    public string ProductName { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public decimal UnitPriceAmount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public Money LineTotal => new(UnitPriceAmount * Quantity, Currency);
}
