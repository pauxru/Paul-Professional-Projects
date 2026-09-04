using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Domain;
using Microsoft.EntityFrameworkCore;

namespace Contoso.Storefront.Infrastructure.Persistence;

public sealed class EfStorefrontStore(StorefrontDbContext dbContext) : IStorefrontStore
{
    public async Task<IReadOnlyList<ProductSnapshot>> ListProductsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.Products
            .AsNoTracking()
            .OrderBy(product => product.Sku)
            .Select(product => new ProductSnapshot(
                product.Id,
                product.Sku,
                product.Name,
                product.Description,
                product.PriceAmount,
                product.Currency,
                product.IsActive))
            .ToListAsync(cancellationToken);
    }

    public Task<ProductSnapshot?> FindProductAsync(Guid id, CancellationToken cancellationToken)
    {
        return dbContext.Products
            .AsNoTracking()
            .Where(product => product.Id == id)
            .Select(product => new ProductSnapshot(
                product.Id,
                product.Sku,
                product.Name,
                product.Description,
                product.PriceAmount,
                product.Currency,
                product.IsActive))
            .SingleOrDefaultAsync(cancellationToken);
    }

    public async Task<OrderSnapshot?> FindOrderByIdempotencyKeyAsync(
        string key,
        CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Include(item => item.Items)
            .SingleOrDefaultAsync(item => item.IdempotencyKey == key, cancellationToken);
        return order is null ? null : MapOrder(order);
    }

    public async Task<OrderSnapshot?> FindOrderAsync(Guid id, CancellationToken cancellationToken)
    {
        var order = await dbContext.Orders
            .AsNoTracking()
            .Include(item => item.Items)
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return order is null ? null : MapOrder(order);
    }

    public async Task SaveOrderWithOutboxAsync(
        Order order,
        OutboxMessage message,
        CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        dbContext.Orders.Add(order);
        dbContext.OutboxMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static OrderSnapshot MapOrder(Order order)
    {
        var items = order.Items
            .OrderBy(item => item.ProductName)
            .Select(item => new OrderItemSnapshot(
                item.ProductId,
                item.ProductName,
                item.Quantity,
                item.UnitPriceAmount,
                item.UnitPriceAmount * item.Quantity,
                item.Currency))
            .ToList();
        return new OrderSnapshot(
            order.Id,
            order.CustomerReference,
            order.IdempotencyKey,
            order.Status.ToString(),
            items.Sum(item => item.LineTotal),
            items.FirstOrDefault()?.Currency ?? "USD",
            order.CreatedAt,
            items);
    }
}
