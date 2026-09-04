using Contoso.Payments.Domain.Common;

namespace Contoso.Payments.Domain.Events;

public sealed record OrderPlaced(
    Guid OrderId,
    string CustomerRef,
    decimal TotalAmount,
    string Currency,
    DateTimeOffset PlacedAtUtc) : DomainEvent;

public sealed record PaymentAuthorized(
    Guid PaymentIntentId,
    Guid OrderId,
    decimal AuthorizedAmount,
    string Currency,
    string ProviderReference,
    DateTimeOffset AuthorizedAtUtc) : DomainEvent;

public sealed record PaymentCaptured(
    Guid PaymentIntentId,
    Guid OrderId,
    decimal CapturedAmount,
    string Currency,
    string ProviderReference,
    DateTimeOffset CapturedAtUtc) : DomainEvent;

public sealed record PaymentFailed(
    Guid PaymentIntentId,
    Guid OrderId,
    string FailureCode,
    string Message,
    DateTimeOffset FailedAtUtc) : DomainEvent;

public sealed record RefundIssued(
    Guid RefundId,
    Guid OrderId,
    Guid PaymentIntentId,
    decimal Amount,
    string Currency,
    DateTimeOffset IssuedAtUtc) : DomainEvent;
