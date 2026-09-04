using Contoso.Storefront.Domain;

namespace Contoso.Storefront.Application.Ports;

public sealed record ProductSnapshot(
    Guid Id,
    string Sku,
    string Name,
    string Description,
    decimal Price,
    string Currency,
    bool IsActive);

public sealed record OrderSnapshot(
    Guid Id,
    string CustomerReference,
    string IdempotencyKey,
    string Status,
    decimal Total,
    string Currency,
    DateTimeOffset CreatedAt,
    IReadOnlyList<OrderItemSnapshot> Items);

public sealed record OrderItemSnapshot(
    Guid ProductId,
    string ProductName,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal,
    string Currency);

public interface IStorefrontStore
{
    Task<IReadOnlyList<ProductSnapshot>> ListProductsAsync(CancellationToken cancellationToken);
    Task<ProductSnapshot?> FindProductAsync(Guid id, CancellationToken cancellationToken);
    Task<OrderSnapshot?> FindOrderByIdempotencyKeyAsync(string key, CancellationToken cancellationToken);
    Task<OrderSnapshot?> FindOrderAsync(Guid id, CancellationToken cancellationToken);
    Task SaveOrderWithOutboxAsync(Order order, OutboxMessage message, CancellationToken cancellationToken);
}
