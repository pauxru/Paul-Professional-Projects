using System.Text.RegularExpressions;

namespace FraudPipeline.Domain.ValueObjects;

public sealed record Money
{
    public decimal Amount { get; init; }
    public string Currency { get; init; } = "USD";

    private static readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "KES", "USD", "EUR", "GBP"
    };

    private Money() { }

    public Money(decimal amount, string currency)
    {
        Amount = amount;
        Currency = currency;
    }

    public static Money Of(decimal amount, string currency)
    {
        if (amount < 0m) throw new ArgumentOutOfRangeException(nameof(amount), "Amount cannot be negative.");
        if (string.IsNullOrWhiteSpace(currency)) throw new ArgumentException("Currency is required.", nameof(currency));
        var normalised = currency.ToUpperInvariant();
        if (!_allowed.Contains(normalised)) throw new ArgumentException($"Unsupported currency '{currency}'.", nameof(currency));
        return new Money(decimal.Round(amount, 4, MidpointRounding.ToZero), normalised);
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    private void EnsureSameCurrency(Money other)
    {
        if (!string.Equals(Currency, other.Currency, StringComparison.Ordinal))
            throw new InvalidOperationException($"Currency mismatch: {Currency} vs {other.Currency}");
    }

    public override string ToString() => $"{Amount:0.####} {Currency}";
}
