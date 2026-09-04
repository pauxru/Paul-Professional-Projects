using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Domain.Interest;

/// <summary>
/// Tracks interest accrued but not yet capitalised for an account. Daily accrual increments the
/// running <see cref="AccruedMinor"/>; capitalisation posts it to the ledger and resets to zero.
/// </summary>
public sealed class InterestAccrual
{
    private InterestAccrual() { } // EF

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }

    /// <summary>The GL account interest is booked against on capitalisation (e.g. interest expense).</summary>
    public Guid CounterAccountId { get; private set; }
    public string Currency { get; private set; } = null!;
    public long AnnualRateBps { get; private set; }
    public DayCountConvention Convention { get; private set; }
    public long AccruedMinor { get; private set; }
    public DateOnly LastAccrualDate { get; private set; }
    public long Version { get; private set; }

    public Money Accrued => new(AccruedMinor, Monetary.Currency.FromCode(Currency));

    public static InterestAccrual Create(
        Guid accountId,
        Guid counterAccountId,
        Currency currency,
        long annualRateBps,
        DayCountConvention convention,
        DateOnly startDate)
    {
        if (annualRateBps < 0)
        {
            throw new DomainException("interest.negative_rate", "Interest rate cannot be negative.");
        }

        return new InterestAccrual
        {
            Id = Guid.NewGuid(),
            AccountId = accountId,
            CounterAccountId = counterAccountId,
            Currency = currency.Code,
            AnnualRateBps = annualRateBps,
            Convention = convention,
            LastAccrualDate = startDate,
        };
    }

    /// <summary>Accrues interest on <paramref name="principalMinor"/> up to <paramref name="asOf"/>.</summary>
    public long AccrueTo(long principalMinor, DateOnly asOf)
    {
        if (asOf <= LastAccrualDate)
        {
            return 0;
        }

        long accrued = InterestCalculator.AccrueForPeriod(
            principalMinor, AnnualRateBps, Convention, LastAccrualDate, asOf);

        AccruedMinor = checked(AccruedMinor + accrued);
        LastAccrualDate = asOf;
        Version++;
        return accrued;
    }

    /// <summary>Returns the amount to capitalise and resets the running accrual to zero.</summary>
    public long Capitalize()
    {
        long amount = AccruedMinor;
        AccruedMinor = 0;
        Version++;
        return amount;
    }
}
