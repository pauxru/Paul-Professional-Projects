namespace Contoso.Payments.Application.Abstractions;

/// <summary>
/// Deterministic simulator port used to represent the outbound payment provider (Stripe / Adyen /
/// M-Pesa / whatever).  Real vendor SDKs would replace the adapter, not this port.
/// </summary>
public interface IPaymentProvider
{
    Task<PaymentProviderResult> AuthorizeAsync(PaymentProviderRequest request, CancellationToken ct);
    Task<PaymentProviderResult> CaptureAsync(string providerReference, CancellationToken ct);
    Task<PaymentProviderResult> VoidAsync(string providerReference, CancellationToken ct);
    Task<PaymentProviderResult> RefundAsync(string providerReference, decimal amount, string currency, CancellationToken ct);
}

public sealed record PaymentProviderRequest(
    Guid PaymentIntentId,
    Guid OrderId,
    decimal Amount,
    string Currency,
    string IdempotencyKey,
    string CustomerRef);

public enum PaymentProviderOutcome
{
    Succeeded = 0,
    Declined = 1,
    Timeout = 2,
    Duplicate = 3,
    AsynchronousPending = 4,
    ProviderError = 5
}

public sealed record PaymentProviderResult(
    PaymentProviderOutcome Outcome,
    string? ProviderReference,
    string? FailureCode,
    string? Message,
    int LatencyMs);
