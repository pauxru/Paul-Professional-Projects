using ExampleBank.Ledger.Domain.Common;

namespace ExampleBank.Ledger.Domain.Monetary;

/// <summary>
/// A monetary amount held as a signed <see cref="long"/> count of <see cref="Monetary.Currency"/>
/// minor units. There is deliberately no <c>double</c> or floating-point path anywhere in the
/// domain: every amount is an exact integer of minor units with an attached currency and scale.
/// </summary>
public readonly record struct Money
{
    public long MinorUnits { get; }
    public Currency Currency { get; }

    public Money(long minorUnits, Currency currency)
    {
        Currency = currency ?? throw new ArgumentNullException(nameof(currency));
        MinorUnits = minorUnits;
    }

    public static Money Zero(Currency currency) => new(0, currency);

    public bool IsZero => MinorUnits == 0;
    public bool IsPositive => MinorUnits > 0;
    public bool IsNegative => MinorUnits < 0;

    /// <summary>Builds money from a major-unit decimal, rejecting values finer than the currency scale.</summary>
    public static Money FromMajor(decimal major, Currency currency)
    {
        var scaled = major * currency.MinorUnitsPerMajor;
        var rounded = Math.Round(scaled, MidpointRounding.ToEven);
        if (scaled != rounded)
        {
            throw new DomainException(
                "ledger.sub_minor_precision",
                $"Amount {major} has more precision than {currency.Code} allows ({currency.Scale} dp).");
        }

        return new Money((long)rounded, currency);
    }

    public decimal ToMajor() => (decimal)MinorUnits / Currency.MinorUnitsPerMajor;

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(MinorUnits + other.MinorUnits), Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(checked(MinorUnits - other.MinorUnits), Currency);
    }

    public Money Negate() => new(checked(-MinorUnits), Currency);

    public Money Abs() => new(Math.Abs(MinorUnits), Currency);

    private void EnsureSameCurrency(Money other)
    {
        if (!Currency.Equals(other.Currency))
        {
            throw new MixedCurrencyException(
                $"Cannot combine {Currency.Code} with {other.Currency.Code}; currencies must match.");
        }
    }

    public override string ToString() => $"{ToMajor():0.00} {Currency.Code}";
}
