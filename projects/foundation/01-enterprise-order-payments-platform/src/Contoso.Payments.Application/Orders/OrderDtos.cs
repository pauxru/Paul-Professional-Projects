namespace Contoso.Payments.Application.Orders;

public sealed record OrderLineRequest(string Sku, int Quantity);

public sealed record PlaceOrderRequest(string CustomerRef, string Currency, IReadOnlyList<OrderLineRequest> Lines);

public sealed record OrderLineDto(Guid Id, string Sku, int Quantity, decimal UnitPrice);

public sealed record OrderDto(
    Guid Id,
    string CustomerRef,
    string Currency,
    string Status,
    decimal Total,
    decimal RefundedTotal,
    Guid? PaymentIntentId,
    IReadOnlyList<OrderLineDto> Lines,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    int Version);
