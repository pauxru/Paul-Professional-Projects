namespace Contoso.Payments.Application.Webhooks;

/// <summary>
/// Payload posted by the payment provider webhook.  Format is deliberately minimal — real
/// vendors send much more but this is enough to exercise the security + state machine.
/// </summary>
public sealed record WebhookPayload(
    string EventType,               // "payment.captured" | "payment.failed"
    Guid PaymentIntentId,
    string ProviderReference,
    decimal Amount,
    string Currency,
    string? FailureCode,
    string? FailureMessage,
    long UnixTimestamp);
