namespace SubscriptionBilling.Domain;

public enum InvoiceStatus
{
    Draft,
    Open,
    Paid,
    Uncollectible,
    Void
}

public enum InvoiceLineType
{
    Recurring,
    Usage,
    Proration,
    OneOff,
    Credit,
    Tax
}

public sealed record InvoiceLine(
    Guid Id,
    InvoiceLineType Type,
    string Description,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd,
    Money Amount,
    bool Taxable,
    bool RevenueRecognizedOverPeriod = false);

public sealed class Invoice
{
    private readonly List<InvoiceLine> _lines = [];

    public Invoice(
        Guid id,
        Guid customerId,
        Guid subscriptionId,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        string currency,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || customerId == Guid.Empty || subscriptionId == Guid.Empty)
        {
            throw new DomainException("Invoice, customer, and subscription ids are required.");
        }

        if (periodEnd <= periodStart)
        {
            throw new DomainException("Invoice period is invalid.");
        }

        Id = id;
        CustomerId = customerId;
        SubscriptionId = subscriptionId;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        Currency = Money.Zero(currency).Currency;
        CreatedAt = createdAt;
        Status = InvoiceStatus.Draft;
    }

    public Guid Id { get; }
    public Guid CustomerId { get; }
    public Guid SubscriptionId { get; }
    public string? Number { get; private set; }
    public DateTimeOffset PeriodStart { get; }
    public DateTimeOffset PeriodEnd { get; }
    public string Currency { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset? FinalizedAt { get; private set; }
    public InvoiceStatus Status { get; private set; }
    public IReadOnlyList<InvoiceLine> Lines => _lines.AsReadOnly();
    public Money Subtotal { get; private set; }
    public Money Discount { get; private set; }
    public Money Tax { get; private set; }
    public Money CreditApplied { get; private set; }
    public Money Total { get; private set; }

    public void AddLine(InvoiceLine line)
    {
        EnsureDraft();
        if (!string.Equals(line.Amount.Currency, Currency, StringComparison.Ordinal))
        {
            throw new DomainException("Invoice line currency does not match invoice currency.");
        }

        _lines.Add(line);
        Recalculate(Money.Zero(Currency), Money.Zero(Currency), Money.Zero(Currency));
    }

    public void Recalculate(
        Money discount,
        Money tax,
        Money creditApplied,
        bool taxInclusive = false)
    {
        EnsureDraft();
        EnsureCurrency(discount);
        EnsureCurrency(tax);
        EnsureCurrency(creditApplied);
        if (discount.MinorUnits < 0 || creditApplied.MinorUnits < 0)
        {
            throw new DomainException("Discount and applied credit cannot be negative.");
        }

        Subtotal = new Money(
            _lines.Aggregate(0L, (current, line) => checked(current + line.Amount.MinorUnits)),
            Currency);
        var positiveSubtotal = Math.Max(0, Subtotal.MinorUnits);
        var clampedDiscount = Math.Min(discount.MinorUnits, positiveSubtotal);
        var beforeCredit = checked(
            Subtotal.MinorUnits -
            clampedDiscount +
            (taxInclusive ? 0 : tax.MinorUnits));
        var clampedCredit = Math.Min(creditApplied.MinorUnits, Math.Max(0, beforeCredit));
        Discount = new Money(clampedDiscount, Currency);
        Tax = tax;
        CreditApplied = new Money(clampedCredit, Currency);
        Total = new Money(Math.Max(0, checked(beforeCredit - clampedCredit)), Currency);
    }

    public void FinalizeInvoice(string number, DateTimeOffset finalizedAt)
    {
        EnsureDraft();
        ArgumentException.ThrowIfNullOrWhiteSpace(number);
        if (_lines.Count == 0)
        {
            throw new DomainException("An invoice must contain at least one line.");
        }

        Number = number.Trim();
        FinalizedAt = finalizedAt;
        Status = InvoiceStatus.Open;
    }

    public void MarkPaid()
    {
        EnsureStatus(InvoiceStatus.Open);
        Status = InvoiceStatus.Paid;
    }

    public void MarkUncollectible()
    {
        EnsureStatus(InvoiceStatus.Open);
        Status = InvoiceStatus.Uncollectible;
    }

    public void Void()
    {
        if (Status is InvoiceStatus.Paid or InvoiceStatus.Void)
        {
            throw new DomainException($"A {Status} invoice cannot be voided.");
        }

        Status = InvoiceStatus.Void;
    }

    private void EnsureDraft()
    {
        if (Status != InvoiceStatus.Draft)
        {
            throw new DomainException("Finalized invoices are immutable.");
        }
    }

    private void EnsureStatus(InvoiceStatus expected)
    {
        if (Status != expected)
        {
            throw new DomainException($"Expected invoice state {expected}, but found {Status}.");
        }
    }

    private void EnsureCurrency(Money amount)
    {
        if (!string.Equals(amount.Currency, Currency, StringComparison.Ordinal))
        {
            throw new DomainException("Invoice calculation currency mismatch.");
        }
    }
}

public sealed record CreditNote(
    Guid Id,
    Guid InvoiceId,
    string Number,
    Money Amount,
    string Reason,
    DateTimeOffset CreatedAt);

public enum CouponType
{
    Percentage,
    FixedAmount
}

public enum CouponDuration
{
    Once,
    Repeating,
    Forever
}

public sealed class Coupon
{
    private int _redemptionCount;

    public Coupon(
        Guid id,
        string code,
        CouponType type,
        decimal percentage,
        Money? fixedAmount,
        CouponDuration duration,
        int? durationCycles,
        int? maxRedemptions,
        int redemptionCount = 0)
    {
        if (id == Guid.Empty)
        {
            throw new DomainException("Coupon id is required.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (type == CouponType.Percentage && (percentage <= 0 || percentage > 100))
        {
            throw new DomainException("Percentage coupons must be greater than zero and at most 100.");
        }

        if (type == CouponType.FixedAmount &&
            (fixedAmount is null || fixedAmount.Value.MinorUnits <= 0))
        {
            throw new DomainException("Fixed coupons require a positive amount.");
        }

        if (duration == CouponDuration.Repeating && durationCycles is null or <= 0)
        {
            throw new DomainException("Repeating coupons require a positive cycle count.");
        }

        if (maxRedemptions is <= 0)
        {
            throw new DomainException("Maximum redemptions must be positive when provided.");
        }

        if (redemptionCount < 0 || (maxRedemptions is not null && redemptionCount > maxRedemptions))
        {
            throw new DomainException("Coupon redemption count is invalid.");
        }

        Id = id;
        Code = code.Trim().ToUpperInvariant();
        Type = type;
        Percentage = percentage;
        FixedAmount = fixedAmount;
        Duration = duration;
        DurationCycles = durationCycles;
        MaxRedemptions = maxRedemptions;
        _redemptionCount = redemptionCount;
    }

    public Guid Id { get; }
    public string Code { get; }
    public CouponType Type { get; }
    public decimal Percentage { get; }
    public Money? FixedAmount { get; }
    public CouponDuration Duration { get; }
    public int? DurationCycles { get; }
    public int? MaxRedemptions { get; }
    public int RedemptionCount => _redemptionCount;

    public Money CalculateDiscount(Money eligibleAmount, int priorSubscriptionApplications)
    {
        if (!CanApply(priorSubscriptionApplications))
        {
            return Money.Zero(eligibleAmount.Currency);
        }

        if (MaxRedemptions is not null && _redemptionCount >= MaxRedemptions.Value)
        {
            throw new DomainException("Coupon redemption limit has been reached.");
        }

        Money discount;
        if (Type == CouponType.Percentage)
        {
            discount = eligibleAmount.Multiply(Percentage / 100m);
        }
        else
        {
            var fixedAmount = FixedAmount!.Value;
            if (!string.Equals(fixedAmount.Currency, eligibleAmount.Currency, StringComparison.Ordinal))
            {
                throw new DomainException("Fixed coupon currency does not match invoice currency.");
            }

            discount = new Money(
                Math.Min(fixedAmount.MinorUnits, Math.Max(0, eligibleAmount.MinorUnits)),
                eligibleAmount.Currency);
        }

        _redemptionCount++;
        return discount;
    }

    private bool CanApply(int priorApplications) =>
        Duration switch
        {
            CouponDuration.Once => priorApplications == 0,
            CouponDuration.Repeating => priorApplications < DurationCycles,
            CouponDuration.Forever => true,
            _ => false
        };
}

public sealed record CreditLot(Guid Id, Money Original, Money Remaining, DateTimeOffset CreatedAt);

public sealed record CreditAllocation(Guid CreditId, Money Amount);

public sealed class AccountCreditLedger(string currency)
{
    private readonly List<CreditLot> _credits = [];
    private readonly string _currency = Money.Zero(currency).Currency;

    public IReadOnlyList<CreditLot> Credits => _credits.AsReadOnly();

    public void Add(Guid id, Money amount, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty || amount.MinorUnits <= 0)
        {
            throw new DomainException("A credit requires an id and positive amount.");
        }

        EnsureCurrency(amount);
        _credits.Add(new CreditLot(id, amount, amount, createdAt));
    }

    public IReadOnlyList<CreditAllocation> Apply(Money amountDue)
    {
        EnsureCurrency(amountDue);
        var remainingDue = Math.Max(0, amountDue.MinorUnits);
        var allocations = new List<CreditAllocation>();

        for (var index = 0; index < _credits.Count && remainingDue > 0; index++)
        {
            var lot = _credits[index];
            var applied = Math.Min(lot.Remaining.MinorUnits, remainingDue);
            if (applied == 0)
            {
                continue;
            }

            var allocation = new Money(applied, _currency);
            allocations.Add(new CreditAllocation(lot.Id, allocation));
            _credits[index] = lot with { Remaining = lot.Remaining - allocation };
            remainingDue -= applied;
        }

        return allocations;
    }

    private void EnsureCurrency(Money money)
    {
        if (!string.Equals(money.Currency, _currency, StringComparison.Ordinal))
        {
            throw new DomainException("Credit currency mismatch.");
        }
    }
}
