using Microsoft.EntityFrameworkCore;
using SampleApi.Models;

namespace SampleApi.Data;

public static class Seed
{
    public static async Task EnsureAsync(CatalogDbContext db, int productCount = 200, int orderCount = 500)
    {
        await db.Database.EnsureCreatedAsync();
        if (!await db.Products.AnyAsync())
        {
            var categories = new[] { "coffee", "grain", "sugar", "electronics", "apparel", "tools", "beverages", "snacks" };
            var rng = new Random(20260903);
            for (var i = 0; i < productCount; i++)
            {
                db.Products.Add(new Product
                {
                    Sku = $"SKU-{i:D5}",
                    Name = $"Sample Product {i}",
                    Description = $"Fictional catalogue entry for demo purposes only ({i}).",
                    Price = Math.Round((decimal)(rng.NextDouble() * 500 + 5), 2),
                    Stock = rng.Next(0, 1000),
                    Category = categories[rng.Next(categories.Length)]
                });
            }
            await db.SaveChangesAsync();
        }

        if (!await db.Orders.AnyAsync() && orderCount > 0)
        {
            var productIds = await db.Products.Select(p => p.Id).ToListAsync();
            if (productIds.Count == 0) return;
            var rng = new Random(20260903);
            for (var i = 0; i < orderCount; i++)
            {
                var lineCount = rng.Next(1, 6);
                var order = new Order
                {
                    CustomerRef = $"CUST-{rng.Next(1, 2000):D5}",
                    CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-i),
                    Status = "Complete"
                };
                decimal total = 0;
                var used = new HashSet<int>();
                for (var j = 0; j < lineCount; j++)
                {
                    int pid;
                    do { pid = productIds[rng.Next(productIds.Count)]; } while (!used.Add(pid));
                    var qty = rng.Next(1, 5);
                    // We need the product price; look it up cheaply.
                    var price = await db.Products.Where(p => p.Id == pid).Select(p => p.Price).FirstAsync();
                    total += price * qty;
                    order.Lines.Add(new OrderLine { ProductId = pid, Quantity = qty, UnitPrice = price });
                }
                order.Total = total;
                db.Orders.Add(order);
                if ((i + 1) % 100 == 0) await db.SaveChangesAsync();
            }
            await db.SaveChangesAsync();
        }
    }
}
