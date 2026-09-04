using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Application.Abstractions;

/// <summary>
/// A foreign-exchange rate expressed as an exact rational (<see cref="Numerator"/> /
/// <see cref="Denominator"/>) so conversions are performed in integer arithmetic with a provable,
/// conserved rounding remainder — never as a lossy floating-point multiply.
/// </summary>
public sealed record FxRate(Currency From, Currency To, long Numerator, long Denominator, DateOnly AsOf)
{
    public decimal AsDecimal => (decimal)Numerator / Denominator;
}

/// <summary>Supplies FX rates. The local adapter reads from a seeded rate table.</summary>
public interface IFxRateProvider
{
    FxRate GetRate(Currency from, Currency to, DateOnly asOf);

    bool TryGetRate(Currency from, Currency to, DateOnly asOf, out FxRate? rate);
}
