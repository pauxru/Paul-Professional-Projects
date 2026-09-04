namespace Contoso.Storefront.Domain;

public sealed class Product
{
    private Product()
    {
    }

    public Product(
        Guid id,
        string sku,
        string name,
        string description,
        Money price,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Product id is required.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(sku))
        {
            throw new ArgumentException("SKU is required.", nameof(sku));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Product name is required.", nameof(name));
        }

        Id = id;
        Sku = sku.Trim().ToUpperInvariant();
        Name = name.Trim();
        Description = description?.Trim() ?? string.Empty;
        PriceAmount = price.Amount;
        Currency = price.Currency;
        CreatedAt = createdAt;
        IsActive = true;
    }

    public Guid Id { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public decimal PriceAmount { get; private set; }
    public string Currency { get; private set; } = "USD";
    public bool IsActive { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public int Version { get; private set; }

    public Money Price => new(PriceAmount, Currency);

    public void UpdateDescription(string description)
    {
        Description = description?.Trim() ?? string.Empty;
        Version++;
    }

    public void UpdatePrice(Money price)
    {
        PriceAmount = price.Amount;
        Currency = price.Currency;
        Version++;
    }

    public void Deactivate()
    {
        IsActive = false;
        Version++;
    }
}
