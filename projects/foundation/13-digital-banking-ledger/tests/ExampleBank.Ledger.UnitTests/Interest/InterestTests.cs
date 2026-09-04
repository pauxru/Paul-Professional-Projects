using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Interest;
using ExampleBank.Ledger.UnitTests.TestSupport;

namespace ExampleBank.Ledger.UnitTests.Interest;

public sealed class InterestTests
{
    [Fact]
    public void AccrueForPeriod_Actual365_OverTenDays()
    {
        // 3,650,000 minor @ 10.00% for 10/365 of a year = 10,000 minor units.
        long interest = InterestCalculator.AccrueForPeriod(
            principalMinor: 3_650_000,
            annualRateBps: 1_000,
            convention: DayCountConvention.Actual365,
            start: new DateOnly(2024, 1, 1),
            end: new DateOnly(2024, 1, 11));

        Assert.Equal(10_000, interest);
    }

    [Fact]
    public void AccrueForPeriod_Thirty360_OverSixMonths()
    {
        // 3,600,000 minor @ 10.00% for 180/360 (= 0.5) of a year = 180,000 minor units.
        long interest = InterestCalculator.AccrueForPeriod(
            principalMinor: 3_600_000,
            annualRateBps: 1_000,
            convention: DayCountConvention.Thirty360,
            start: new DateOnly(2024, 1, 1),
            end: new DateOnly(2024, 7, 1));

        Assert.Equal(180_000, interest);
    }

    [Fact]
    public void AccrueForPeriod_DrivenByFakeClock_UsesClockAsPeriodEnd()
    {
        var clock = new FakeClock(2024, 7, 1);
        var start = new DateOnly(2024, 1, 1);

        long interest = InterestCalculator.AccrueForPeriod(
            3_600_000, 1_000, DayCountConvention.Thirty360, start, ((IClock)clock).Today);

        Assert.Equal(180_000, interest);
    }

    [Fact]
    public void Accrue_NegativePrincipal_Throws()
    {
        Assert.Throws<DomainException>(() => InterestCalculator.Accrue(-1, 1_000, 1m));
    }

    [Fact]
    public void Accrue_NegativeRate_Throws()
    {
        Assert.Throws<DomainException>(() => InterestCalculator.Accrue(1_000, -1, 1m));
    }

    [Fact]
    public void Accrue_ZeroRate_YieldsZero()
    {
        Assert.Equal(0, InterestCalculator.Accrue(1_000_000, 0, 1m));
    }

    [Fact]
    public void Actual365Convention_YearFraction_IsActualDaysOver365()
    {
        var convention = DayCountConventions.For(DayCountConvention.Actual365);

        Assert.Equal(10m / 365m, convention.YearFraction(new DateOnly(2024, 1, 1), new DateOnly(2024, 1, 11)));
    }

    [Fact]
    public void Thirty360Convention_AdjustsDay31ToDay30()
    {
        var convention = DayCountConventions.For(DayCountConvention.Thirty360);

        // 31 Jan -> 31 Mar: d1 31->30, then d2 31->30 => 60/360.
        Assert.Equal(60m / 360m, convention.YearFraction(new DateOnly(2024, 1, 31), new DateOnly(2024, 3, 31)));
    }

    [Fact]
    public void Thirty360Convention_EndOfMonthShortFebruary()
    {
        var convention = DayCountConventions.For(DayCountConvention.Thirty360);

        // 31 Jan -> 29 Feb: d1 31->30, d2 stays 29 => 29/360.
        Assert.Equal(29m / 360m, convention.YearFraction(new DateOnly(2024, 1, 31), new DateOnly(2024, 2, 29)));
    }
}
