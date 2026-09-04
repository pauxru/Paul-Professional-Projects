using System.Globalization;

namespace Lakehouse.Domain.Money;

/// <summary>
/// A currency amount. Money is never a double: amounts are decimal and always carry a currency so
/// mixed-currency arithmetic is impossible without an explicit conversion through the FX dimension.
/// </summary>
public readonly record struct Money(decimal Amount, string Currency)
{
    public static Money Of(decimal amount, string currency) => new(amount, Normalize(currency));

    private static string Normalize(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new ArgumentException("Currency is required.", nameof(currency));
        return currency.Trim().ToUpperInvariant();
    }

    public Money ConvertTo(string targetCurrency, decimal rateToTarget)
    {
        if (rateToTarget <= 0) throw new ArgumentOutOfRangeException(nameof(rateToTarget), "FX rate must be positive.");
        return new Money(decimal.Round(Amount * rateToTarget, 4, MidpointRounding.ToEven), Normalize(targetCurrency));
    }

    public override string ToString() => $"{Amount.ToString("0.####", CultureInfo.InvariantCulture)} {Currency}";
}
