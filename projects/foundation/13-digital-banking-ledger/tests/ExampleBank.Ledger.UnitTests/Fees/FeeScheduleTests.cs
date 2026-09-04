using ExampleBank.Ledger.Domain.Fees;

namespace ExampleBank.Ledger.UnitTests.Fees;

public sealed class FeeScheduleTests
{
    private static readonly Guid Income = Guid.NewGuid();

    [Fact]
    public void FixedFee_IgnoresBaseAmount()
    {
        var schedule = FeeSchedule.CreateFixed("WIRE", "Flat wire", "USD", fixedAmountMinor: 500, Income);

        Assert.Equal(500, schedule.Calculate(0));
        Assert.Equal(500, schedule.Calculate(1_000_000));
    }

    [Fact]
    public void PercentageFee_AppliesBasisPoints()
    {
        var schedule = FeeSchedule.CreatePercentage("PCT", "Percent", "KES", rateBps: 150, Income);

        // 1.50% of 2,000,000 = 30,000 minor units.
        Assert.Equal(30_000, schedule.Calculate(2_000_000));
    }

    [Fact]
    public void PercentageFee_BelowMinimum_ClampsToMinimum()
    {
        var schedule = FeeSchedule.CreatePercentage(
            "PCT", "Percent", "KES", rateBps: 150, Income, minFeeMinor: 2_000, maxFeeMinor: 50_000);

        // 1.50% of 100,000 = 1,500 which is below the 2,000 floor.
        Assert.Equal(2_000, schedule.Calculate(100_000));
    }

    [Fact]
    public void PercentageFee_AboveMaximum_ClampsToMaximum()
    {
        var schedule = FeeSchedule.CreatePercentage(
            "PCT", "Percent", "KES", rateBps: 150, Income, minFeeMinor: 2_000, maxFeeMinor: 50_000);

        // 1.50% of 5,000,000 = 75,000 which exceeds the 50,000 cap.
        Assert.Equal(50_000, schedule.Calculate(5_000_000));
    }

    [Fact]
    public void TieredFee_SumsMarginalBands()
    {
        var schedule = TieredSchedule();

        // Band 1: 100,000 @ 1.00% = 1,000. Band 2: 50,000 @ 0.50% = 250. Total 1,250.
        Assert.Equal(1_250, schedule.Calculate(150_000));
    }

    [Fact]
    public void TieredFee_TinyBase_ClampsToMinimum()
    {
        var schedule = TieredSchedule();

        Assert.Equal(100, schedule.Calculate(1));
    }

    [Fact]
    public void TieredFee_SpansAllBands()
    {
        var schedule = TieredSchedule();

        // Band 1: 100,000 @ 1.00% = 1,000; Band 2: 900,000 @ 0.50% = 4,500;
        // Band 3: 100,000 @ 0.25% = 250. Raw 5,750 -> capped at 25,000 max (not hit here).
        Assert.Equal(5_750, schedule.Calculate(1_100_000));
    }

    [Fact]
    public void CreateTiered_WithNoTiers_Throws()
    {
        Assert.Throws<ExampleBank.Ledger.Domain.Common.DomainException>(() =>
            FeeSchedule.CreateTiered("T", "T", "USD", Array.Empty<FeeTier>(), Income));
    }

    private static FeeSchedule TieredSchedule() => FeeSchedule.CreateTiered(
        "TIER-USD", "Tiered", "USD",
        new[]
        {
            FeeTier.Create(0, upToMinor: 100_000, rateBps: 100),
            FeeTier.Create(1, upToMinor: 1_000_000, rateBps: 50),
            FeeTier.Create(2, upToMinor: long.MaxValue, rateBps: 25),
        },
        Income, minFeeMinor: 100, maxFeeMinor: 25_000);
}
