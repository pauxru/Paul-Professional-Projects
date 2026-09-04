using SubscriptionBilling.Domain;

namespace SubscriptionBilling.UnitTests;

public sealed class SubscriptionAndProrationTests
{
    [Fact]
    public void BillingAnchor_January31NonLeapYear_PreservesMonthEndThroughMarch()
    {
        var start = At(2025, 1, 31);
        var anchor = BillingCycleAnchor.From(start);
        var interval = new BillingInterval(BillingIntervalUnit.Month, 1);

        var february = anchor.Next(start, interval);
        var march = anchor.Next(february, interval);

        Assert.Equal(At(2025, 2, 28), february);
        Assert.Equal(At(2025, 3, 31), march);
    }

    [Fact]
    public void BillingAnchor_January31LeapYear_UsesFebruary29ThenMarch31()
    {
        var start = At(2024, 1, 31);
        var anchor = BillingCycleAnchor.From(start);
        var interval = new BillingInterval(BillingIntervalUnit.Month, 1);

        var february = anchor.Next(start, interval);
        var march = anchor.Next(february, interval);

        Assert.Equal(At(2024, 2, 29), february);
        Assert.Equal(At(2024, 3, 31), march);
    }

    [Fact]
    public void BillingAnchor_April30MonthEnd_MovesToMay31()
    {
        var start = At(2026, 4, 30);
        var anchor = BillingCycleAnchor.From(start);

        var next = anchor.Next(start, new BillingInterval(BillingIntervalUnit.Month, 1));

        Assert.Equal(At(2026, 5, 31), next);
    }

    [Fact]
    public void BillingAnchor_LeapDayAnnual_RestoresLeapDayInNextLeapYear()
    {
        var start = At(2024, 2, 29);
        var anchor = BillingCycleAnchor.From(start);
        var interval = new BillingInterval(BillingIntervalUnit.Year, 1);

        var first = anchor.Next(start, interval);
        var second = anchor.Next(first, interval);
        var third = anchor.Next(second, interval);
        var fourth = anchor.Next(third, interval);

        Assert.Equal(At(2025, 2, 28), first);
        Assert.Equal(At(2028, 2, 29), fourth);
    }

    [Fact]
    public void Subscription_TrialToActivePastDueUnpaidAndRecovery_ControlsAccess()
    {
        var trialEnd = At(2026, 2, 1);
        var clock = new ReliabilityTests.FakeClock(At(2026, 1, 1));
        var subscription = NewSubscription(trialEnd);

        clock.Set(trialEnd);
        subscription.CompleteTrial(
            clock.UtcNow,
            TrialEndBehavior.Activate,
            new BillingInterval(BillingIntervalUnit.Month, 1));
        subscription.MarkPastDue();
        subscription.MarkUnpaid();

        Assert.Equal(SubscriptionState.Unpaid, subscription.State);
        Assert.True(subscription.IsAccessSuspended);

        subscription.RecoverFromPayment();

        Assert.Equal(SubscriptionState.Active, subscription.State);
        Assert.False(subscription.IsAccessSuspended);
    }

    [Fact]
    public void Subscription_PauseAndResume_ReturnsToActive()
    {
        var subscription = NewSubscription();

        subscription.Pause();
        subscription.Resume();

        Assert.Equal(SubscriptionState.Active, subscription.State);
    }

    [Fact]
    public void Subscription_CancelAtPeriodEnd_CancelsWhenPeriodAdvances()
    {
        var subscription = NewSubscription();
        subscription.Cancel(immediately: false);

        subscription.AdvancePeriod(At(2026, 3, 1));

        Assert.Equal(SubscriptionState.Canceled, subscription.State);
        Assert.True(subscription.IsAccessSuspended);
    }

    [Fact]
    public void Subscription_ImmediateCancelThenReactivate_RestoresAccess()
    {
        var subscription = NewSubscription();
        subscription.Cancel(immediately: true);

        subscription.Reactivate(At(2026, 4, 1), At(2026, 5, 1));

        Assert.Equal(SubscriptionState.Active, subscription.State);
        Assert.False(subscription.IsAccessSuspended);
    }

    [Fact]
    public void Proration_UpgradeMidCycle_ReconcilesCreditAndChargeToNet()
    {
        var result = ProrationEngine.Calculate(
            new Money(10_000, "USD"),
            new Money(20_000, "USD"),
            At(2026, 1, 1),
            At(2026, 2, 1),
            At(2026, 1, 16),
            ProrationBehavior.CreateProrations);

        Assert.Equal(result.NetAmount.MinorUnits, result.Lines.Sum(line => line.Amount.MinorUnits));
        Assert.True(result.NetAmount.MinorUnits > 0);
    }

    [Fact]
    public void Proration_DowngradeMidCycle_ReconcilesToNegativeCredit()
    {
        var result = ProrationEngine.Calculate(
            new Money(20_000, "USD"),
            new Money(10_000, "USD"),
            At(2026, 1, 1),
            At(2026, 2, 1),
            At(2026, 1, 16),
            ProrationBehavior.CreateProrations);

        Assert.Equal(result.NetAmount.MinorUnits, result.Lines.Sum(line => line.Amount.MinorUnits));
        Assert.True(result.NetAmount.MinorUnits < 0);
    }

    [Fact]
    public void Proration_QuantityChange_UsesOldAndNewExtendedPrices()
    {
        var unitPrice = new PerUnitPricing(new Money(1_000, "USD"));
        var result = ProrationEngine.Calculate(
            unitPrice.Calculate(2),
            unitPrice.Calculate(5),
            At(2026, 1, 1),
            At(2026, 2, 1),
            At(2026, 1, 16),
            ProrationBehavior.CreateProrations);

        Assert.Equal(result.NetAmount.MinorUnits, result.Lines.Sum(line => line.Amount.MinorUnits));
        Assert.True(result.NetAmount.MinorUnits > 0);
    }

    [Fact]
    public void Proration_SameDayAtPeriodStart_ChargesExactFullDifference()
    {
        var start = At(2026, 1, 1);
        var result = ProrationEngine.Calculate(
            new Money(10_001, "USD"),
            new Money(20_004, "USD"),
            start,
            At(2026, 2, 1),
            start,
            ProrationBehavior.AlwaysInvoice);

        Assert.Equal(10_003, result.NetAmount.MinorUnits);
        Assert.Equal(10_003, result.Lines.Sum(line => line.Amount.MinorUnits));
        Assert.True(result.InvoiceImmediately);
    }

    [Fact]
    public void Proration_MultipleChangesInOneCycle_EachReconcilesAndTotalsExactly()
    {
        var start = At(2026, 1, 1);
        var end = At(2026, 2, 1);
        var first = ProrationEngine.Calculate(
            new Money(10_000, "USD"),
            new Money(16_000, "USD"),
            start,
            end,
            At(2026, 1, 10),
            ProrationBehavior.CreateProrations);
        var second = ProrationEngine.Calculate(
            new Money(16_000, "USD"),
            new Money(13_000, "USD"),
            start,
            end,
            At(2026, 1, 20),
            ProrationBehavior.CreateProrations);

        var lineTotal = first.Lines.Concat(second.Lines).Sum(line => line.Amount.MinorUnits);
        var intendedTotal = first.NetAmount.MinorUnits + second.NetAmount.MinorUnits;

        Assert.Equal(intendedTotal, lineTotal);
    }

    [Fact]
    public void Proration_None_CreatesNoLines()
    {
        var result = ProrationEngine.Calculate(
            new Money(10_000, "USD"),
            new Money(20_000, "USD"),
            At(2026, 1, 1),
            At(2026, 2, 1),
            At(2026, 1, 15),
            ProrationBehavior.None);

        Assert.Empty(result.Lines);
        Assert.Equal(0, result.NetAmount.MinorUnits);
    }

    private static Subscription NewSubscription(DateTimeOffset? trialEnd = null)
    {
        var start = At(2026, 1, 1);
        return new Subscription(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            start,
            At(2026, 2, 1),
            BillingCycleAnchor.From(start),
            trialEnd);
    }

    private static DateTimeOffset At(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);
}
