using System.Text.Json;
using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Domain;

namespace Contoso.Storefront.Application.Orders;

public sealed record CreateOrderItem(Guid ProductId, int Quantity);
public sealed record CreateOrderCommand(string CustomerReference, string IdempotencyKey, IReadOnlyList<CreateOrderItem> Items);
public sealed record CreateOrderResult(OrderSnapshot Order, bool IsReplay);

public sealed class OrderService(
    IStorefrontStore store,
    IClock clock,
    IIdGenerator idGenerator)
{
    public async Task<CreateOrderResult> CreateAsync(
        CreateOrderCommand command,
        CancellationToken cancellationToken)
    {
        var replay = await store.FindOrderByIdempotencyKeyAsync(command.IdempotencyKey, cancellationToken);
        if (replay is not null)
        {
            return new CreateOrderResult(replay, true);
        }

        var order = new Order(
            idGenerator.NewId(),
            command.CustomerReference,
            command.IdempotencyKey,
            clock.UtcNow);

        foreach (var requestedItem in command.Items)
        {
            var product = await store.FindProductAsync(requestedItem.ProductId, cancellationToken)
                ?? throw new ProductNotFoundException(requestedItem.ProductId);

            if (!product.IsActive)
            {
                throw new InvalidOperationException($"Product {product.Id} is not active.");
            }

            order.AddItem(
                product.Id,
                product.Name,
                requestedItem.Quantity,
                new Money(product.Price, product.Currency));
        }

        order.Submit();
        var message = new OutboxMessage(
            idGenerator.NewId(),
            "order.submitted.v1",
            JsonSerializer.Serialize(new
            {
                order.Id,
                order.CustomerReference,
                Total = order.Total.Amount,
                order.Total.Currency
            }),
            clock.UtcNow);

        await store.SaveOrderWithOutboxAsync(order, message, cancellationToken);

        var saved = await store.FindOrderAsync(order.Id, cancellationToken)
            ?? throw new InvalidOperationException("The saved order could not be reloaded.");
        return new CreateOrderResult(saved, false);
    }
}

public sealed class ProductNotFoundException(Guid productId)
    : Exception($"Product {productId} was not found.")
{
    public Guid ProductId { get; } = productId;
}
