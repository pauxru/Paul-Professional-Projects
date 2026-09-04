namespace Northstar.Domain.Common;

public readonly record struct Money
{
    public Money(decimal amount, string currency)
    {
        if (string.IsNullOrWhiteSpace(currency) || currency.Trim().Length != 3)
        {
            throw new DomainRuleException("Currency must be an ISO-style three-letter code.");
        }

        Amount = amount;
        Currency = currency.Trim().ToUpperInvariant();
    }

    public decimal Amount { get; }
    public string Currency { get; }

    public static Money Zero(string currency) => new(0m, currency);
}
