using System.ComponentModel.DataAnnotations;
using Lab.Domain.Entities;

namespace Lab.Application.Contracts;

public sealed record CreateOrderRequest(
    [property: Required, StringLength(120, MinimumLength = 2)] string? CustomerName,
    [property: Required, EmailAddress] string? CustomerEmail,
    [property: Required, StringLength(48, MinimumLength = 3)] string? Reference,
    [property: Required, StringLength(160, MinimumLength = 3)] string? Destination);

public sealed record OrderSummary(
    Guid Id,
    string Reference,
    string CustomerName,
    string Destination,
    OrderStatus Status,
    DateTimeOffset CreatedAt);

public sealed record ShipmentResponse(
    Guid Id,
    string TrackingNumber,
    DateTimeOffset DispatchedAt,
    DateTimeOffset? DeliveredAt);

public sealed record OrderDetails(
    Guid Id,
    string Reference,
    string CustomerName,
    string CustomerEmail,
    string Destination,
    OrderStatus Status,
    DateTimeOffset CreatedAt,
    ShipmentResponse? Shipment);

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record TokenRequest(
    [property: Required, StringLength(80, MinimumLength = 2)] string? Subject,
    [property: Required, StringLength(120, MinimumLength = 3)] string? Scope);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresInSeconds);
