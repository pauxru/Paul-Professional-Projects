using SubscriptionBilling.Domain;

namespace SubscriptionBilling.UnitTests;

public sealed class PricingTests
{
    [Fact]
    public void Money_BankersAndAwayFromZeroRounding_AreExplicit()
    {
        var money = new Money(1, "USD");

        Assert.Equal(0, money.Multiply(0.5m, MonetaryRounding.Bankers).MinorUnits);
        Assert.Equal(1, money.Multiply(0.5m, MonetaryRounding.AwayFromZero).MinorUnits);
    }

    [Fact]
    public void Money_DifferentCurrencies_RejectArithmetic()
    {
        Assert.Throws<DomainException>(() =>
            new Money(100, "USD") + new Money(100, "KES"));
    }

    [Theory]
    [InlineData(0, 10_000)]
    [InlineData(1, 10_000)]
    [InlineData(500, 10_000)]
    public void FlatRecurring_AnyQuantity_ReturnsFee(long units, long expected)
    {
        var strategy = new FlatRecurringPricing(new Money(10_000, "USD"));

        Assert.Equal(expected, strategy.Calculate(units).MinorUnits);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 25)]
    [InlineData(100, 2_500)]
    [InlineData(101, 2_525)]
    public void PerUnit_Boundaries_MultiplyExactly(long units, long expected)
    {
        var strategy = new PerUnitPricing(new Money(25, "USD"));

        Assert.Equal(expected, strategy.Calculate(units).MinorUnits);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 10)]
    [InlineData(100, 1_000)]
    [InlineData(101, 1_008)]
    [InlineData(200, 1_800)]
    [InlineData(201, 1_805)]
    [InlineData(350, 2_550)]
    public void Tiered_EveryBoundary_PricesEachTierCumulatively(long units, long expected)
    {
        var strategy = Tiered();

        Assert.Equal(expected, strategy.Calculate(units).MinorUnits);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 10)]
    [InlineData(100, 1_000)]
    [InlineData(101, 808)]
    [InlineData(200, 1_600)]
    [InlineData(201, 1_005)]
    [InlineData(350, 1_750)]
    public void Volume_EveryBoundary_PricesAllUnitsAtReachedTier(long units, long expected)
    {
        var strategy = Volume();

        Assert.Equal(expected, strategy.Calculate(units).MinorUnits);
    }

    [Theory]
    [InlineData(0, 5_000)]
    [InlineData(1, 5_000)]
    [InlineData(100, 5_000)]
    [InlineData(101, 5_003)]
    [InlineData(250, 5_450)]
    public void GraduatedOverage_AllowanceBoundary_ChargesOnlyOverage(long units, long expected)
    {
        var strategy = new GraduatedOveragePricing(
            new Money(5_000, "USD"),
            100,
            new Money(3, "USD"));

        Assert.Equal(expected, strategy.Calculate(units).MinorUnits);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1_000)]
    [InlineData(10, 1_000)]
    [InlineData(11, 2_000)]
    [InlineData(20, 2_000)]
    [InlineData(21, 3_000)]
    public void Package_BlockBoundary_RoundsUp(long units, long expected)
    {
        var strategy = new PackagePricing(10, new Money(1_000, "EUR"));

        Assert.Equal(expected, strategy.Calculate(units).MinorUnits);
    }

    [Theory]
    [InlineData(2_500)]
    [InlineData(-2_500)]
    public void OneOff_PositiveAndNegativeCharges_AreSupported(long amount)
    {
        var strategy = new OneOffPricing(new Money(amount, "KES"));

        Assert.Equal(amount, strategy.Calculate(0).MinorUnits);
    }

    [Fact]
    public void PricingFactory_MissingRequiredConfiguration_RejectsDefinition()
    {
        var configuration = new PricingConfiguration(PricingModel.Package);

        Assert.Throws<DomainException>(() =>
            PricingStrategyFactory.Create(configuration, "USD"));
    }

    private static TieredPricing Tiered() =>
        new(
        [
            new PriceTier(100, new Money(10, "USD")),
            new PriceTier(200, new Money(8, "USD")),
            new PriceTier(null, new Money(5, "USD"))
        ]);

    private static VolumePricing Volume() =>
        new(
        [
            new PriceTier(100, new Money(10, "USD")),
            new PriceTier(200, new Money(8, "USD")),
            new PriceTier(null, new Money(5, "USD"))
        ]);
}
