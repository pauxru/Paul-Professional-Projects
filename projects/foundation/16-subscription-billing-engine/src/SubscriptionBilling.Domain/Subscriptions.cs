namespace SubscriptionBilling.Domain;

public enum SubscriptionState
{
    Trialing,
    Active,
    PastDue,
    Unpaid,
    Canceled,
    Paused
}

public enum TrialEndBehavior
{
    Activate,
    Cancel,
    Pause
}

public sealed class Subscription
{
    private SubscriptionState? _stateBeforePause;

    public Subscription(
        Guid id,
        Guid customerId,
        Guid planVersionId,
        long quantity,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        BillingCycleAnchor anchor,
        DateTimeOffset? trialEnd = null)
    {
        if (id == Guid.Empty || customerId == Guid.Empty || planVersionId == Guid.Empty)
        {
            throw new DomainException("Subscription, customer, and plan version ids are required.");
        }

        if (quantity <= 0)
        {
            throw new DomainException("Subscription quantity must be positive.");
        }

        if (periodEnd <= periodStart)
        {
            throw new DomainException("Subscription period end must be after its start.");
        }

        if (trialEnd is not null && trialEnd <= periodStart)
        {
            throw new DomainException("Trial end must be after subscription start.");
        }

        Id = id;
        CustomerId = customerId;
        PlanVersionId = planVersionId;
        Quantity = quantity;
        CurrentPeriodStart = periodStart;
        CurrentPeriodEnd = periodEnd;
        Anchor = anchor;
        TrialEnd = trialEnd;
        State = trialEnd is null ? SubscriptionState.Active : SubscriptionState.Trialing;
    }

    public Guid Id { get; }
    public Guid CustomerId { get; }
    public Guid PlanVersionId { get; private set; }
    public long Quantity { get; private set; }
    public SubscriptionState State { get; private set; }
    public DateTimeOffset CurrentPeriodStart { get; private set; }
    public DateTimeOffset CurrentPeriodEnd { get; private set; }
    public BillingCycleAnchor Anchor { get; }
    public DateTimeOffset? TrialEnd { get; }
    public bool CancelAtPeriodEnd { get; private set; }
    public bool IsAccessSuspended { get; private set; }

    public void CompleteTrial(
        DateTimeOffset now,
        TrialEndBehavior behavior,
        BillingInterval interval)
    {
        EnsureState(SubscriptionState.Trialing);
        if (TrialEnd is null || now < TrialEnd.Value)
        {
            throw new DomainException("Trial has not ended.");
        }

        switch (behavior)
        {
            case TrialEndBehavior.Activate:
                State = SubscriptionState.Active;
                CurrentPeriodStart = TrialEnd.Value;
                CurrentPeriodEnd = Anchor.Next(CurrentPeriodStart, interval);
                break;
            case TrialEndBehavior.Cancel:
                State = SubscriptionState.Canceled;
                break;
            case TrialEndBehavior.Pause:
                _stateBeforePause = SubscriptionState.Active;
                State = SubscriptionState.Paused;
                break;
            default:
                throw new DomainException("Unsupported trial end behavior.");
        }
    }

    public void Activate()
    {
        EnsureState(SubscriptionState.Trialing);
        State = SubscriptionState.Active;
    }

    public void MarkPastDue()
    {
        EnsureState(SubscriptionState.Active);
        State = SubscriptionState.PastDue;
    }

    public void MarkUnpaid()
    {
        EnsureState(SubscriptionState.PastDue);
        State = SubscriptionState.Unpaid;
        IsAccessSuspended = true;
    }

    public void RecoverFromPayment()
    {
        if (State is not (SubscriptionState.PastDue or SubscriptionState.Unpaid))
        {
            throw new DomainException("Only past-due or unpaid subscriptions can recover from payment.");
        }

        State = SubscriptionState.Active;
        IsAccessSuspended = false;
    }

    public void Pause()
    {
        if (State is not (SubscriptionState.Active or SubscriptionState.Trialing or SubscriptionState.PastDue))
        {
            throw new DomainException($"A {State} subscription cannot be paused.");
        }

        _stateBeforePause = State;
        State = SubscriptionState.Paused;
    }

    public void Resume()
    {
        EnsureState(SubscriptionState.Paused);
        State = _stateBeforePause is SubscriptionState.Trialing
            ? SubscriptionState.Trialing
            : SubscriptionState.Active;
        _stateBeforePause = null;
    }

    public void Cancel(bool immediately)
    {
        if (State == SubscriptionState.Canceled)
        {
            return;
        }

        if (immediately)
        {
            State = SubscriptionState.Canceled;
            CancelAtPeriodEnd = false;
            IsAccessSuspended = true;
            return;
        }

        if (State is SubscriptionState.Unpaid)
        {
            throw new DomainException("An unpaid subscription must be canceled immediately.");
        }

        CancelAtPeriodEnd = true;
    }

    public void AdvancePeriod(DateTimeOffset nextPeriodEnd)
    {
        if (State == SubscriptionState.Canceled)
        {
            throw new DomainException("Canceled subscriptions cannot advance.");
        }

        if (CancelAtPeriodEnd)
        {
            State = SubscriptionState.Canceled;
            IsAccessSuspended = true;
            CancelAtPeriodEnd = false;
            return;
        }

        if (nextPeriodEnd <= CurrentPeriodEnd)
        {
            throw new DomainException("Next period end must be after the current period end.");
        }

        CurrentPeriodStart = CurrentPeriodEnd;
        CurrentPeriodEnd = nextPeriodEnd;
    }

    public void ChangePlan(Guid planVersionId, long quantity)
    {
        if (State is SubscriptionState.Canceled or SubscriptionState.Unpaid or SubscriptionState.Paused)
        {
            throw new DomainException($"A {State} subscription cannot change plan.");
        }

        if (planVersionId == Guid.Empty || quantity <= 0)
        {
            throw new DomainException("A valid plan version and positive quantity are required.");
        }

        PlanVersionId = planVersionId;
        Quantity = quantity;
    }

    public void Reactivate(DateTimeOffset periodStart, DateTimeOffset periodEnd)
    {
        EnsureState(SubscriptionState.Canceled);
        if (periodEnd <= periodStart)
        {
            throw new DomainException("Reactivation period is invalid.");
        }

        CurrentPeriodStart = periodStart;
        CurrentPeriodEnd = periodEnd;
        CancelAtPeriodEnd = false;
        IsAccessSuspended = false;
        State = SubscriptionState.Active;
    }

    private void EnsureState(SubscriptionState expected)
    {
        if (State != expected)
        {
            throw new DomainException($"Expected state {expected}, but subscription is {State}.");
        }
    }
}

public enum ProrationBehavior
{
    CreateProrations,
    None,
    AlwaysInvoice
}

public enum ProrationLineKind
{
    Credit,
    Charge
}

public sealed record ProrationLine(
    ProrationLineKind Kind,
    string Description,
    Money Amount,
    DateTimeOffset PeriodStart,
    DateTimeOffset PeriodEnd);

public sealed record ProrationResult(
    IReadOnlyList<ProrationLine> Lines,
    Money NetAmount,
    bool InvoiceImmediately);

public static class ProrationEngine
{
    public static ProrationResult Calculate(
        Money oldPeriodPrice,
        Money newPeriodPrice,
        DateTimeOffset periodStart,
        DateTimeOffset periodEnd,
        DateTimeOffset changeAt,
        ProrationBehavior behavior,
        MonetaryRounding rounding = MonetaryRounding.Bankers)
    {
        if (!string.Equals(oldPeriodPrice.Currency, newPeriodPrice.Currency, StringComparison.Ordinal))
        {
            throw new DomainException("Proration prices must use the same currency.");
        }

        if (periodEnd <= periodStart || changeAt < periodStart || changeAt > periodEnd)
        {
            throw new DomainException("Proration timestamps are outside the billing period.");
        }

        if (behavior == ProrationBehavior.None)
        {
            return new ProrationResult(
                [],
                Money.Zero(oldPeriodPrice.Currency),
                false);
        }

        var totalTicks = periodEnd.UtcTicks - periodStart.UtcTicks;
        var remainingTicks = periodEnd.UtcTicks - changeAt.UtcTicks;
        var remainingRatio = totalTicks == 0
            ? 0m
            : (decimal)remainingTicks / totalTicks;

        var creditMinor = -Money.RoundMinorUnits(
            oldPeriodPrice.MinorUnits * remainingRatio,
            rounding);
        var intendedNet = Money.RoundMinorUnits(
            (newPeriodPrice.MinorUnits - oldPeriodPrice.MinorUnits) * remainingRatio,
            rounding);
        var chargeMinor = checked(intendedNet - creditMinor);

        var lines = new List<ProrationLine>();
        if (creditMinor != 0)
        {
            lines.Add(new ProrationLine(
                ProrationLineKind.Credit,
                "Unused time on previous price",
                new Money(creditMinor, oldPeriodPrice.Currency),
                changeAt,
                periodEnd));
        }

        if (chargeMinor != 0)
        {
            lines.Add(new ProrationLine(
                ProrationLineKind.Charge,
                "Remaining time on new price",
                new Money(chargeMinor, newPeriodPrice.Currency),
                changeAt,
                periodEnd));
        }

        return new ProrationResult(
            lines,
            new Money(intendedNet, oldPeriodPrice.Currency),
            behavior == ProrationBehavior.AlwaysInvoice);
    }
}
