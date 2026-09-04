using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Events;

namespace Contoso.Payments.Domain.Payments;

/// <summary>
/// Payment intent aggregate.  Explicit state machine:
/// <c>Requires → Authorized → Captured</c> (happy path) with
/// <c>Requires → Failed</c>, <c>Authorized → Voided</c>, <c>Authorized → Failed</c> also valid.
/// </summary>
public sealed class PaymentIntent : AggregateRoot
{
    private readonly List<PaymentAttempt> _attempts = new();

    private PaymentIntent()
    {
        IdempotencyKey = string.Empty;
        ProviderReference = string.Empty;
        Amount = new Money(0m, "USD");
        CapturedAmount = new Money(0m, "USD");
    }

    public PaymentIntent(Guid id, Guid orderId, Money amount, string idempotencyKey, DateTimeOffset createdAtUtc)
    {
        if (id == Guid.Empty) throw new DomainException("Payment intent id required.");
        if (orderId == Guid.Empty) throw new DomainException("Order id required.");
        if (!amount.IsPositive) throw new DomainException("Payment amount must be positive.");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new DomainException("Idempotency key required.");
        Id = id;
        OrderId = orderId;
        Amount = amount;
        CapturedAmount = new Money(0m, amount.Currency);
        Status = PaymentIntentStatus.Requires;
        IdempotencyKey = idempotencyKey;
        CreatedAtUtc = createdAtUtc;
        UpdatedAtUtc = createdAtUtc;
        ProviderReference = string.Empty;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Money Amount { get; private set; }
    public Money CapturedAmount { get; private set; }
    public PaymentIntentStatus Status { get; private set; }
    public string IdempotencyKey { get; private set; }
    public string ProviderReference { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public IReadOnlyList<PaymentAttempt> Attempts => _attempts;

    public void RecordAttempt(PaymentAttempt attempt)
    {
        _attempts.Add(attempt);
        Version++;
    }

    public void MarkAuthorized(string providerReference, DateTimeOffset nowUtc)
    {
        if (Status != PaymentIntentStatus.Requires)
            throw new DomainException($"Cannot authorize from status {Status}.", "payment.illegal_transition");
        ProviderReference = providerReference;
        Status = PaymentIntentStatus.Authorized;
        UpdatedAtUtc = nowUtc;
        Version++;
        Raise(new PaymentAuthorized(Id, OrderId, Amount.Amount, Amount.Currency, providerReference, nowUtc));
    }

    public void MarkCaptured(DateTimeOffset nowUtc)
    {
        if (Status != PaymentIntentStatus.Authorized)
            throw new DomainException($"Cannot capture from status {Status}.", "payment.illegal_transition");
        CapturedAmount = new Money(Amount.Amount, Amount.Currency);
        Status = PaymentIntentStatus.Captured;
        UpdatedAtUtc = nowUtc;
        Version++;
        Raise(new PaymentCaptured(Id, OrderId, Amount.Amount, Amount.Currency, ProviderReference, nowUtc));
    }

    public void MarkVoided(DateTimeOffset nowUtc)
    {
        if (Status != PaymentIntentStatus.Authorized)
            throw new DomainException($"Cannot void from status {Status}.", "payment.illegal_transition");
        Status = PaymentIntentStatus.Voided;
        UpdatedAtUtc = nowUtc;
        Version++;
    }

    public void MarkFailed(string code, string message, DateTimeOffset nowUtc)
    {
        if (Status is PaymentIntentStatus.Captured or PaymentIntentStatus.Voided)
            throw new DomainException($"Cannot fail from status {Status}.", "payment.illegal_transition");
        Status = PaymentIntentStatus.Failed;
        UpdatedAtUtc = nowUtc;
        Version++;
        Raise(new PaymentFailed(Id, OrderId, code, message, nowUtc));
    }
}

/// <summary>A single attempt against the payment provider (for provider retry visibility).</summary>
public sealed class PaymentAttempt
{
    private PaymentAttempt() { Outcome = string.Empty; }

    public PaymentAttempt(Guid id, int attemptNumber, string outcome, string? providerReference,
        DateTimeOffset attemptedAtUtc, int latencyMs)
    {
        Id = id;
        AttemptNumber = attemptNumber;
        Outcome = outcome;
        ProviderReference = providerReference;
        AttemptedAtUtc = attemptedAtUtc;
        LatencyMs = latencyMs;
    }

    public Guid Id { get; private set; }
    public int AttemptNumber { get; private set; }
    public string Outcome { get; private set; }
    public string? ProviderReference { get; private set; }
    public DateTimeOffset AttemptedAtUtc { get; private set; }
    public int LatencyMs { get; private set; }
}
