using System.Globalization;

namespace ReconEngine.Domain.ValueObjects;

/// <summary>
/// Currency-aware money value object. Amounts are held as integer <b>minor units</b> (e.g. cents)
/// in a <see cref="long"/> so that arithmetic is exact and free of binary floating-point error.
/// Two money values may only be combined when their currencies match.
/// </summary>
public readonly record struct Money
{
    public long MinorUnits { get; }
    public string Currency { get; }

    public Money(long minorUnits, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));
        Currency = currency.Trim().ToUpperInvariant();
        MinorUnits = minorUnits;
    }

    public int Decimals => CurrencyInfo.DecimalsFor(Currency);

    public decimal ToMajor() => decimal.Divide(MinorUnits, CurrencyInfo.ScaleFor(Currency));

    /// <summary>Build a money value from a human-readable major amount, rounding half-to-even.</summary>
    public static Money FromMajor(decimal major, string currency)
    {
        var scale = CurrencyInfo.ScaleFor(currency);
        var minor = (long)Math.Round(major * scale, 0, MidpointRounding.ToEven);
        return new Money(minor, currency);
    }

    public static Money Zero(string currency) => new(0, currency);

    public bool IsZero => MinorUnits == 0;
    public bool IsNegative => MinorUnits < 0;
    public bool IsPositive => MinorUnits > 0;

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

    public Money Negate() => new(-MinorUnits, Currency);

    public Money Abs() => new(Math.Abs(MinorUnits), Currency);

    /// <summary>Absolute difference in minor units. Currencies must match.</summary>
    public long AbsoluteDifferenceMinor(Money other)
    {
        EnsureSameCurrency(other);
        return Math.Abs(MinorUnits - other.MinorUnits);
    }

    public bool SameCurrency(Money other) => string.Equals(Currency, other.Currency, StringComparison.Ordinal);

    private void EnsureSameCurrency(Money other)
    {
        if (!SameCurrency(other))
            throw new InvalidOperationException($"Currency mismatch: {Currency} vs {other.Currency}.");
    }

    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{ToMajor().ToString("F" + Decimals, CultureInfo.InvariantCulture)} {Currency}");
}
