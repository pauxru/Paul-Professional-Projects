using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Domain.Holds;

public enum HoldStatus
{
    Active = 1,
    Captured = 2,
    PartiallyCaptured = 3,
    Released = 4,
    Expired = 5,
}

/// <summary>
/// An authorization hold reduces an account's <em>available</em> balance without moving cleared
/// funds. It is later captured (fully or partially, releasing any remainder), released, or expired
/// by the background sweeper. Every terminal transition frees the full held amount on the account.
/// </summary>
public sealed class Hold
{
    private Hold() { } // EF

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public long AmountMinor { get; private set; }
    public long CapturedMinor { get; private set; }
    public string Currency { get; private set; } = null!;
    public HoldStatus Status { get; private set; }
    public string? Reference { get; private set; }
    public string? IdempotencyKey { get; private set; }
    public DateTimeOffset PlacedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public long Version { get; private set; }

    public long RemainingMinor => AmountMinor - CapturedMinor;
    public bool IsActive => Status == HoldStatus.Active;
    public Money Amount => new(AmountMinor, Monetary.Currency.FromCode(Currency));

    public static Hold Create(
        Guid accountId,
        Money amount,
        DateTimeOffset placedAt,
        DateTimeOffset expiresAt,
        string? reference,
        string? idempotencyKey)
    {
        if (amount.MinorUnits <= 0)
        {
            throw new DomainException("hold.non_positive", "Hold amount must be positive.");
        }

        if (expiresAt <= placedAt)
        {
            throw new DomainException("hold.invalid_expiry", "Hold expiry must be after placement.");
        }

        return new Hold
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            AmountMinor = amount.MinorUnits,
            Currency = amount.Currency.Code,
            Status = HoldStatus.Active,
            Reference = reference?.Trim(),
            IdempotencyKey = idempotencyKey,
            PlacedAt = placedAt,
            ExpiresAt = expiresAt,
        };
    }

    private void EnsureActive(string action)
    {
        if (Status != HoldStatus.Active)
        {
            throw new DomainException("hold.not_active", $"Cannot {action} a hold that is {Status}.");
        }
    }

    /// <summary>
    /// Captures <paramref name="captureMinor"/> of the hold (defaulting to the full amount) and
    /// resolves it, releasing any uncaptured remainder back to the available balance.
    /// </summary>
    public long Capture(long captureMinor, DateTimeOffset now)
    {
        EnsureActive("capture");
        if (captureMinor <= 0 || captureMinor > AmountMinor)
        {
            throw new DomainException(
                "hold.invalid_capture",
                $"Capture amount {captureMinor} must be in (0, {AmountMinor}].");
        }

        CapturedMinor = captureMinor;
        Status = captureMinor == AmountMinor ? HoldStatus.Captured : HoldStatus.PartiallyCaptured;
        ResolvedAt = now;
        Version++;
        return AmountMinor; // full original amount is released from the account's held total
    }

    public void Release(DateTimeOffset now)
    {
        EnsureActive("release");
        Status = HoldStatus.Released;
        ResolvedAt = now;
        Version++;
    }

    public void Expire(DateTimeOffset now)
    {
        EnsureActive("expire");
        Status = HoldStatus.Expired;
        ResolvedAt = now;
        Version++;
    }
}
