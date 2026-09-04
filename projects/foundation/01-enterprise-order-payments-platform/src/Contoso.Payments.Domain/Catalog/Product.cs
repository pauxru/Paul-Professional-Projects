using Contoso.Payments.Domain.Common;

namespace Contoso.Payments.Domain.Catalog;

/// <summary>
/// A sellable product in the Contoso Retail catalogue.  Prices are held as <see cref="Money"/>
/// so multi-currency pricing is a first-class concept, not a bolt-on.
/// </summary>
public sealed class Product : AggregateRoot
{
    private Product() { Sku = string.Empty; Name = string.Empty; Price = new Money(0m, "USD"); }

    public Product(Guid id, string sku, string name, Money price)
    {
        if (id == Guid.Empty) throw new DomainException("Product id is required.");
        if (string.IsNullOrWhiteSpace(sku)) throw new DomainException("Product SKU is required.");
        if (string.IsNullOrWhiteSpace(name)) throw new DomainException("Product name is required.");
        if (!price.IsPositive) throw new DomainException("Product price must be positive.");

        Id = id;
        Sku = sku.Trim().ToUpperInvariant();
        Name = name.Trim();
        Price = price;
        IsActive = true;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public string Sku { get; private set; }
    public string Name { get; private set; }
    public Money Price { get; private set; }
    public bool IsActive { get; private set; }

    public void Deactivate()
    {
        IsActive = false;
        Version++;
    }

    public void Reprice(Money newPrice)
    {
        if (!newPrice.IsPositive) throw new DomainException("Price must be positive.");
        if (!string.Equals(newPrice.Currency, Price.Currency, StringComparison.OrdinalIgnoreCase))
            throw new DomainException("Currency cannot be changed via reprice.");
        Price = newPrice;
        Version++;
    }
}
