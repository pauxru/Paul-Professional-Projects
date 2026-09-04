using ExampleBank.Ledger.Domain.Common;

namespace ExampleBank.Ledger.Domain.Interest;

/// <summary>
/// Pure interest arithmetic. Interest = principal × annualRate × yearFraction, computed in
/// <see cref="decimal"/> (never <c>double</c>) and rounded to whole minor units. The rate is
/// expressed in basis points so callers never pass a floating-point rate.
/// </summary>
public static class InterestCalculator
{
    /// <summary>Accrues interest over an explicit year fraction.</summary>
    public static long Accrue(long principalMinor, long annualRateBps, decimal yearFraction)
    {
        if (principalMinor < 0)
        {
            throw new DomainException("interest.negative_principal", "Principal cannot be negative.");
        }

        if (annualRateBps < 0)
        {
            throw new DomainException("interest.negative_rate", "Interest rate cannot be negative.");
        }

        decimal amount = (decimal)principalMinor * annualRateBps / 10000m * yearFraction;
        return (long)Math.Round(amount, MidpointRounding.ToEven);
    }

    /// <summary>Accrues interest for a date range under the given day-count convention.</summary>
    public static long AccrueForPeriod(
        long principalMinor,
        long annualRateBps,
        DayCountConvention convention,
        DateOnly start,
        DateOnly end)
    {
        var yearFraction = DayCountConventions.For(convention).YearFraction(start, end);
        return Accrue(principalMinor, annualRateBps, yearFraction);
    }
}
