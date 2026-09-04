using Microsoft.EntityFrameworkCore;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Domain.Catalog;
using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Inventory;
using Contoso.Payments.Infrastructure.Persistence;

namespace Contoso.Payments.Api.Startup;

public static class DevSeeder
{
    /// <summary>Idempotent — checks for existing rows.</summary>
    public static async Task SeedAsync(AppDbContext db, IIdGenerator ids, CancellationToken ct)
    {
        if (await db.Products.AnyAsync(ct)) return;

        var products = new (string Sku, string Name, decimal Price, string Currency, int Stock)[]
        {
            ("CR-USD-001", "Contoso Retail Wireless Mouse", 24.99m, "USD", 500),
            ("CR-USD-002", "Contoso Retail Mechanical Keyboard", 129.00m, "USD", 120),
            ("CR-USD-003", "Contoso Retail USB-C Charger 65W", 39.50m, "USD", 300),
            ("CR-KES-001", "Contoso Retail Solar Lantern", 3999.00m, "KES", 80),
            ("CR-KES-002", "Contoso Retail Reusable Bag Set", 899.00m, "KES", 400)
        };

        foreach (var p in products)
        {
            var product = new Product(ids.NewGuid(), p.Sku, p.Name, Money.Of(p.Price, p.Currency));
            db.Products.Add(product);
            db.Inventory.Add(new InventoryItem(product.Id, product.Sku, p.Stock));
        }
        await db.SaveChangesAsync(ct);
    }
}
