using SubscriptionBilling.Domain;
using SubscriptionBilling.Application;
using SubscriptionBilling.Infrastructure;

namespace SubscriptionBilling.UnitTests;

public sealed class UsageFinanceAndReportingTests
{
    [Fact]
    public void UsageAggregation_Sum_IncludesAdjustmentEvents()
    {
        var meter = Meter(UsageAggregationMode.Sum);
        var events = new[]
        {
            Usage("one", 10m),
            Usage("two", 5m),
            Usage("correction", -2m, adjustmentOf: "two")
        };

        Assert.Equal(13m, UsageAggregator.Aggregate(meter, events));
    }

    [Fact]
    public void UsageAggregation_Max_ReturnsPeak()
    {
        var events = new[] { Usage("one", 10m), Usage("two", 25m), Usage("three", 20m) };

        Assert.Equal(25m, UsageAggregator.Aggregate(Meter(UsageAggregationMode.Max), events));
    }

    [Fact]
    public void UsageAggregation_LastValue_UsesTimestampThenEventId()
    {
        var timestamp = At(2026, 1, 1);
        var events = new[]
        {
            Usage("a", 10m, timestamp),
            Usage("b", 20m, timestamp)
        };

        Assert.Equal(20m, UsageAggregator.Aggregate(Meter(UsageAggregationMode.LastValue), events));
    }

    [Fact]
    public void UsageAggregation_UniqueCount_DeduplicatesKeys()
    {
        var events = new[]
        {
            Usage("one", 1m, uniqueKey: "device-1"),
            Usage("two", 1m, uniqueKey: "device-1"),
            Usage("three", 1m, uniqueKey: "device-2")
        };

        Assert.Equal(2m, UsageAggregator.Aggregate(Meter(UsageAggregationMode.UniqueCount), events));
    }

    [Theory]
    [InlineData(UsageRoundingMode.Up, "10.01", 11)]
    [InlineData(UsageRoundingMode.Down, "10.99", 10)]
    [InlineData(UsageRoundingMode.NearestBankers, "10.5", 10)]
    [InlineData(UsageRoundingMode.NearestAwayFromZero, "10.5", 11)]
    public void MeterRounding_Mode_IsApplied(
        UsageRoundingMode mode,
        string rawValue,
        long expected)
    {
        var meter = new MeterDefinition(
            Guid.NewGuid(),
            "requests",
            "request",
            UsageAggregationMode.Sum,
            1m,
            mode);

        Assert.Equal(expected, meter.RoundToBillableUnits(decimal.Parse(rawValue)));
    }

    [Fact]
    public void LateUsage_OpenPeriod_IsAccepted()
    {
        Assert.Equal(
            LateUsageDisposition.AcceptedInOpenPeriod,
            LateUsagePolicy.Decide(false, ClosedPeriodUsageBehavior.Reject));
    }

    [Theory]
    [InlineData(ClosedPeriodUsageBehavior.Reject, LateUsageDisposition.RejectedClosedPeriod)]
    [InlineData(ClosedPeriodUsageBehavior.CreditNextOpenPeriod, LateUsageDisposition.CreditedToNextOpenPeriod)]
    public void LateUsage_ClosedPeriod_UsesConfiguredBehavior(
        ClosedPeriodUsageBehavior behavior,
        LateUsageDisposition expected)
    {
        Assert.Equal(expected, LateUsagePolicy.Decide(true, behavior));
    }

    [Fact]
    public void Coupon_Once_AppliesOnlyFirstCycle()
    {
        var coupon = PercentageCoupon(CouponDuration.Once);

        var first = coupon.CalculateDiscount(new Money(10_000, "USD"), 0);
        var second = coupon.CalculateDiscount(new Money(10_000, "USD"), 1);

        Assert.Equal(1_000, first.MinorUnits);
        Assert.Equal(0, second.MinorUnits);
    }

    [Fact]
    public void Coupon_Repeating_StopsAfterConfiguredCycles()
    {
        var coupon = PercentageCoupon(CouponDuration.Repeating, durationCycles: 2);

        Assert.Equal(1_000, coupon.CalculateDiscount(new Money(10_000, "USD"), 0).MinorUnits);
        Assert.Equal(1_000, coupon.CalculateDiscount(new Money(10_000, "USD"), 1).MinorUnits);
        Assert.Equal(0, coupon.CalculateDiscount(new Money(10_000, "USD"), 2).MinorUnits);
    }

    [Fact]
    public void Coupon_RedemptionLimit_IsEnforced()
    {
        var coupon = PercentageCoupon(CouponDuration.Forever, maxRedemptions: 1);
        _ = coupon.CalculateDiscount(new Money(10_000, "USD"), 0);

        Assert.Throws<DomainException>(() =>
            coupon.CalculateDiscount(new Money(10_000, "USD"), 0));
    }

    [Fact]
    public void Coupon_FixedCurrencyMismatch_IsRejected()
    {
        var coupon = new Coupon(
            Guid.NewGuid(),
            "USD500",
            CouponType.FixedAmount,
            0,
            new Money(500, "USD"),
            CouponDuration.Forever,
            null,
            null);

        Assert.Throws<DomainException>(() =>
            coupon.CalculateDiscount(new Money(1_000, "KES"), 0));
    }

    [Fact]
    public void AccountCredits_AreAppliedOldestFirst()
    {
        var ledger = new AccountCreditLedger("USD");
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        ledger.Add(first, new Money(500, "USD"), At(2026, 1, 1));
        ledger.Add(second, new Money(800, "USD"), At(2026, 1, 2));

        var allocations = ledger.Apply(new Money(900, "USD"));

        Assert.Equal(first, allocations[0].CreditId);
        Assert.Equal(500, allocations[0].Amount.MinorUnits);
        Assert.Equal(second, allocations[1].CreditId);
        Assert.Equal(400, allocations[1].Amount.MinorUnits);
    }

    [Fact]
    public void Tax_ExclusiveKenyanVat_AddsSixteenPercent()
    {
        var result = TaxCalculator.Calculate(
            [new TaxLine(Guid.NewGuid(), new Money(10_000, "KES"))],
            0.16m,
            TaxPricingMode.Exclusive,
            TaxRoundingLevel.Line,
            exempt: false,
            reverseCharge: false);

        Assert.Equal(1_600, result.TotalTax.MinorUnits);
    }

    [Fact]
    public void Tax_Inclusive_ExtractsTaxWithoutIncreasingGross()
    {
        var result = TaxCalculator.Calculate(
            [new TaxLine(Guid.NewGuid(), new Money(11_600, "KES"))],
            0.16m,
            TaxPricingMode.Inclusive,
            TaxRoundingLevel.Line,
            exempt: false,
            reverseCharge: false);

        Assert.Equal(1_600, result.TotalTax.MinorUnits);
    }

    [Fact]
    public void Tax_LineAndInvoiceRounding_CanDifferAndAreExplicit()
    {
        var lines = new[]
        {
            new TaxLine(Guid.NewGuid(), new Money(3, "USD")),
            new TaxLine(Guid.NewGuid(), new Money(3, "USD"))
        };

        var line = TaxCalculator.Calculate(
            lines, 0.16m, TaxPricingMode.Exclusive, TaxRoundingLevel.Line, false, false);
        var invoice = TaxCalculator.Calculate(
            lines, 0.16m, TaxPricingMode.Exclusive, TaxRoundingLevel.Invoice, false, false);

        Assert.Equal(0, line.TotalTax.MinorUnits);
        Assert.Equal(1, invoice.TotalTax.MinorUnits);
    }

    [Fact]
    public void Tax_ReverseChargeAndExempt_ProduceZero()
    {
        var lines = new[] { new TaxLine(Guid.NewGuid(), new Money(10_000, "EUR")) };

        Assert.Equal(0, TaxCalculator.Calculate(
            lines, 0.20m, TaxPricingMode.Exclusive, TaxRoundingLevel.Line, true, false)
            .TotalTax.MinorUnits);
        Assert.Equal(0, TaxCalculator.Calculate(
            lines, 0.20m, TaxPricingMode.Exclusive, TaxRoundingLevel.Line, false, true)
            .TotalTax.MinorUnits);
    }

    [Fact]
    public void LocalTaxProvider_ConfiguredUsRate_IsApplied()
    {
        ITaxProvider provider = new LocalTaxProvider(0.0825m);

        var result = provider.Calculate(new TaxRequest(
            "US",
            [new TaxLine(Guid.NewGuid(), new Money(10_000, "USD"))],
            TaxPricingMode.Exclusive,
            TaxRoundingLevel.Invoice,
            false,
            false));

        Assert.Equal(825, result.TotalTax.MinorUnits);
    }

    [Fact]
    public void MrrMovement_AllCategories_ReconcileOpeningToClosing()
    {
        var existing = Guid.NewGuid();
        var expanded = Guid.NewGuid();
        var contracted = Guid.NewGuid();
        var churned = Guid.NewGuid();
        var reactivated = Guid.NewGuid();
        var created = Guid.NewGuid();
        var interval = new BillingInterval(BillingIntervalUnit.Month, 1);
        var previous = new[]
        {
            Snapshot(existing, 10_000, interval),
            Snapshot(expanded, 10_000, interval),
            Snapshot(contracted, 10_000, interval),
            Snapshot(churned, 10_000, interval)
        };
        var current = new[]
        {
            Snapshot(existing, 10_000, interval),
            Snapshot(expanded, 15_000, interval),
            Snapshot(contracted, 8_000, interval),
            new RecurringContractSnapshot(churned, new Money(10_000, "USD"), interval, false, true),
            new RecurringContractSnapshot(reactivated, new Money(7_000, "USD"), interval, true, true),
            new RecurringContractSnapshot(created, new Money(6_000, "USD"), interval, true, false)
        };

        var movement = RevenueReporting.CalculateMovement(previous, current, "USD");

        Assert.Equal(6_000, movement.New.MinorUnits);
        Assert.Equal(5_000, movement.Expansion.MinorUnits);
        Assert.Equal(2_000, movement.Contraction.MinorUnits);
        Assert.Equal(10_000, movement.Churn.MinorUnits);
        Assert.Equal(7_000, movement.Reactivation.MinorUnits);
        Assert.Equal(
            movement.Closing.MinorUnits,
            movement.Opening.MinorUnits +
            movement.New.MinorUnits +
            movement.Expansion.MinorUnits +
            movement.Reactivation.MinorUnits -
            movement.Contraction.MinorUnits -
            movement.Churn.MinorUnits);
    }

    [Fact]
    public void DeferredRevenueSchedule_SumsExactlyToInvoiceAmount()
    {
        var schedule = RevenueRecognition.SpreadEvenly(
            new Money(10_001, "USD"),
            At(2026, 1, 1),
            At(2026, 2, 1));

        Assert.Equal(31, schedule.Count);
        Assert.Equal(10_001, schedule.Sum(entry => entry.Amount.MinorUnits));
    }

    [Fact]
    public void Invoice_FinalizedFinancialData_IsImmutable()
    {
        var invoice = NewInvoice();
        invoice.AddLine(Line(10_000));
        invoice.FinalizeInvoice("INV-000001", At(2026, 2, 1));

        Assert.Throws<DomainException>(() => invoice.AddLine(Line(500)));
        Assert.Throws<DomainException>(() => invoice.Recalculate(
            Money.Zero("USD"), Money.Zero("USD"), Money.Zero("USD")));
    }

    [Fact]
    public void Invoice_DiscountTaxAndCredit_AreAppliedInOrder()
    {
        var invoice = NewInvoice();
        invoice.AddLine(Line(10_000));

        invoice.Recalculate(
            new Money(1_000, "USD"),
            new Money(1_440, "USD"),
            new Money(2_000, "USD"));

        Assert.Equal(8_440, invoice.Total.MinorUnits);
    }

    [Fact]
    public void Invoice_Lifecycle_CanVoidOpenOrMarkPaidButCannotVoidPaid()
    {
        var openToVoid = NewInvoice();
        openToVoid.AddLine(Line(1_000));
        openToVoid.FinalizeInvoice("INV-000010", At(2026, 2, 1));
        openToVoid.Void();

        var openToPaid = NewInvoice();
        openToPaid.AddLine(Line(1_000));
        openToPaid.FinalizeInvoice("INV-000011", At(2026, 2, 1));
        openToPaid.MarkPaid();

        Assert.Equal(InvoiceStatus.Void, openToVoid.Status);
        Assert.Equal(InvoiceStatus.Paid, openToPaid.Status);
        Assert.Throws<DomainException>(openToPaid.Void);
    }

    [Fact]
    public void Plan_NewVersion_DoesNotRewriteHistoricalVersion()
    {
        var plan = new Plan(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Growth",
            new BillingInterval(BillingIntervalUnit.Month, 1));
        var first = plan.AddVersion(
            Guid.NewGuid(),
            At(2026, 1, 1),
            "USD",
            new PricingConfiguration(PricingModel.FlatRecurring, FlatFeeMinor: 10_000),
            false);
        _ = plan.AddVersion(
            Guid.NewGuid(),
            At(2026, 2, 1),
            "USD",
            new PricingConfiguration(PricingModel.FlatRecurring, FlatFeeMinor: 12_000),
            false);

        Assert.Equal(10_000, first.CreatePricingStrategy().Calculate(1).MinorUnits);
        Assert.Equal(1, first.Version);
        Assert.Equal(first.Id, plan.VersionEffectiveAt(At(2026, 1, 15)).Id);
    }

    private static MeterDefinition Meter(UsageAggregationMode mode) =>
        new(Guid.NewGuid(), "meter", "unit", mode, 1m, UsageRoundingMode.Up);

    private static UsageEvent Usage(
        string id,
        decimal quantity,
        DateTimeOffset? at = null,
        string? uniqueKey = null,
        string? adjustmentOf = null) =>
        new(
            id,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            at ?? At(2026, 1, 1),
            quantity,
            uniqueKey,
            adjustmentOf);

    private static Coupon PercentageCoupon(
        CouponDuration duration,
        int? durationCycles = null,
        int? maxRedemptions = null) =>
        new(
            Guid.NewGuid(),
            "TENOFF",
            CouponType.Percentage,
            10m,
            null,
            duration,
            durationCycles,
            maxRedemptions);

    private static RecurringContractSnapshot Snapshot(
        Guid id,
        long amount,
        BillingInterval interval) =>
        new(id, new Money(amount, "USD"), interval, true, false);

    private static Invoice NewInvoice() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            At(2026, 1, 1),
            At(2026, 2, 1),
            "USD",
            At(2026, 1, 1));

    private static InvoiceLine Line(long amount) =>
        new(
            Guid.NewGuid(),
            InvoiceLineType.Recurring,
            "Recurring service",
            At(2026, 1, 1),
            At(2026, 2, 1),
            new Money(amount, "USD"),
            true,
            true);

    private static DateTimeOffset At(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);
}
