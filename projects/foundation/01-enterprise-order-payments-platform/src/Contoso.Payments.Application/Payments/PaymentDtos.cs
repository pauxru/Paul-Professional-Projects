namespace Contoso.Payments.Application.Payments;

public sealed record AuthorizePaymentRequest(Guid OrderId);

public sealed record PaymentIntentDto(
    Guid Id,
    Guid OrderId,
    decimal Amount,
    decimal CapturedAmount,
    string Currency,
    string Status,
    string ProviderReference,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<PaymentAttemptDto> Attempts);

public sealed record PaymentAttemptDto(
    Guid Id, int AttemptNumber, string Outcome, string? ProviderReference,
    DateTimeOffset AttemptedAtUtc, int LatencyMs);
