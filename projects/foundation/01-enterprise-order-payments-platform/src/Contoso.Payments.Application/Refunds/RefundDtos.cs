namespace Contoso.Payments.Application.Refunds;

public sealed record IssueRefundRequest(Guid OrderId, decimal Amount, string Reason);

public sealed record RefundDto(
    Guid Id,
    Guid OrderId,
    Guid PaymentIntentId,
    decimal Amount,
    string Currency,
    string Reason,
    DateTimeOffset IssuedAtUtc);
