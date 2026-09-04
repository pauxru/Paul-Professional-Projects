using Contoso.Payments.Domain.Common;

namespace Contoso.Payments.Domain.Ledger;

public enum LedgerEntryKind
{
    Capture = 0,
    Refund = 1,
    Fee = 2,
    Adjustment = 3
}

/// <summary>
/// Append-only double-entry-lite ledger row.  Amount is stored in minor units (cents) as a
/// <see cref="long"/> so the ledger cannot suffer from decimal drift.
/// </summary>
public sealed class LedgerEntry
{
    private LedgerEntry() { Currency = string.Empty; ReferenceType = string.Empty; }

    public LedgerEntry(Guid id, Guid orderId, Guid? paymentIntentId, Guid? refundId,
        LedgerEntryKind kind, long minorUnits, string currency, string referenceType,
        DateTimeOffset postedAtUtc)
    {
        if (id == Guid.Empty) throw new DomainException("Ledger id required.");
        if (string.IsNullOrWhiteSpace(currency)) throw new DomainException("Currency required.");
        Id = id;
        OrderId = orderId;
        PaymentIntentId = paymentIntentId;
        RefundId = refundId;
        Kind = kind;
        MinorUnits = minorUnits;
        Currency = currency.Trim().ToUpperInvariant();
        ReferenceType = referenceType;
        PostedAtUtc = postedAtUtc;
    }

    public Guid Id { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid? PaymentIntentId { get; private set; }
    public Guid? RefundId { get; private set; }
    public LedgerEntryKind Kind { get; private set; }
    public long MinorUnits { get; private set; }
    public string Currency { get; private set; }
    public string ReferenceType { get; private set; }
    public DateTimeOffset PostedAtUtc { get; private set; }

    public Money AsMoney() => Money.FromMinorUnits(MinorUnits, Currency);
}
