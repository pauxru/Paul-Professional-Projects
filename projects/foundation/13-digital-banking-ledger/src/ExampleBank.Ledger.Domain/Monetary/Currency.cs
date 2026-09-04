using ExampleBank.Ledger.Domain.Common;

namespace ExampleBank.Ledger.Domain.Monetary;

/// <summary>
/// An ISO-4217-style currency with an explicit minor-unit <see cref="Scale"/>.
/// The ledger only supports a fixed, seeded set so that scale is always known and money
/// arithmetic is exact integer arithmetic on minor units.
/// </summary>
public sealed record Currency
{
    public string Code { get; }

    /// <summary>Number of decimal places, i.e. 10^Scale minor units per major unit (KES/USD/EUR = 2).</summary>
    public int Scale { get; }

    private Currency(string code, int scale)
    {
        Code = code;
        Scale = scale;
    }

    public static readonly Currency KES = new("KES", 2);
    public static readonly Currency USD = new("USD", 2);
    public static readonly Currency EUR = new("EUR", 2);

    private static readonly IReadOnlyDictionary<string, Currency> Known =
        new Dictionary<string, Currency>(StringComparer.OrdinalIgnoreCase)
        {
            [KES.Code] = KES,
            [USD.Code] = USD,
            [EUR.Code] = EUR,
        };

    public static IReadOnlyCollection<Currency> All => (IReadOnlyCollection<Currency>)Known.Values;

    public static bool IsKnown(string code) => code is not null && Known.ContainsKey(code);

    public static Currency FromCode(string code)
    {
        if (code is not null && Known.TryGetValue(code, out var currency))
        {
            return currency;
        }

        throw new DomainException("ledger.unknown_currency", $"Currency '{code}' is not supported.");
    }

    /// <summary>Number of minor units in one major unit (e.g. 100 for a 2-scale currency).</summary>
    public long MinorUnitsPerMajor => (long)Math.Pow(10, Scale);

    public override string ToString() => Code;
}
