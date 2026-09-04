namespace SampleApi.Models;

public sealed class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int Stock { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class Order
{
    public int Id { get; set; }
    public string CustomerRef { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public decimal Total { get; set; }
    public string Status { get; set; } = "Pending";
    public List<OrderLine> Lines { get; set; } = new();
}

public sealed class OrderLine
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public int Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public Product? Product { get; set; }
}

public sealed record CreateOrderRequest(string CustomerRef, IReadOnlyList<CreateOrderLine> Lines);
public sealed record CreateOrderLine(int ProductId, int Quantity);
public sealed record OrderSummary(int Id, string CustomerRef, decimal Total, string Status, DateTimeOffset CreatedAt, int LineCount);
