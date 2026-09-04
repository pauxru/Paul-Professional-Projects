namespace Contoso.Payments.Domain.Common;

/// <summary>
/// A monetary amount plus its ISO currency code.  Never use a raw <see cref="decimal"/>
/// for money in this domain.  Arithmetic is only defined between values of the same currency.
/// Implemented as a class-based record so EF Core can own it as a value object.
/// </summary>
public sealed record Money
{
    private static readonly HashSet<string> AllowedCurrencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "USD", "KES"
    };

    // Parameterless ctor for EF materialisation; not intended for callers.
    public Money() { Amount = 0m; Currency = "USD"; }

    public Money(decimal Amount, string Currency)
    {
        this.Amount = Amount;
        this.Currency = Normalize(Currency);
    }

    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";

    public static Money Zero(string currency) => new(0m, currency);
    public static Money Of(decimal amount, string currency) => new(amount, currency);

    private static string Normalize(string currency)
    {
        if (string.IsNullOrWhiteSpace(currency))
            throw new DomainException("Currency must be supplied.");
        var upper = currency.Trim().ToUpperInvariant();
        if (!AllowedCurrencies.Contains(upper))
            throw new DomainException($"Currency '{currency}' is not supported.  Allowed: USD, KES.");
        return upper;
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money Multiply(int quantity)
    {
        if (quantity < 0) throw new DomainException("Cannot multiply money by a negative quantity.");
        return new Money(Amount * quantity, Currency);
    }

    public bool IsZero => Amount == 0m;
    public bool IsPositive => Amount > 0m;
    public bool IsNegative => Amount < 0m;

    public long ToMinorUnits() => (long)Math.Round(Amount * 100m, MidpointRounding.ToEven);

    public static Money FromMinorUnits(long minor, string currency)
        => new(minor / 100m, currency);

    public bool GreaterThan(Money other)
    {
        EnsureSameCurrency(other);
        return Amount > other.Amount;
    }

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.OrdinalIgnoreCase))
            throw new DomainException($"Currency mismatch: {Currency} vs {other.Currency}.");
    }

    public override string ToString() => $"{Amount:0.00} {Currency}";
}
