using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.Infrastructure.Fx;

/// <summary>
/// A local, seeded FX rate provider. Rates are exact rationals (numerator/denominator) between the
/// supported currencies so conversions are performed in integer arithmetic. Rates are illustrative
/// values for the fictional Example Bank, not live market data.
/// </summary>
public sealed class SeededFxRateProvider : IFxRateProvider
{
    private readonly IReadOnlyDictionary<(string From, string To), (long Num, long Den)> _rates;

    public SeededFxRateProvider()
    {
        _rates = new Dictionary<(string, string), (long, long)>
        {
            [("USD", "KES")] = (130, 1),
            [("KES", "USD")] = (1, 130),
            [("EUR", "USD")] = (108, 100),
            [("USD", "EUR")] = (100, 108),
            [("EUR", "KES")] = (140, 1),
            [("KES", "EUR")] = (1, 140),
        };
    }

    public FxRate GetRate(Currency from, Currency to, DateOnly asOf)
    {
        if (!TryGetRate(from, to, asOf, out var rate) || rate is null)
        {
            throw new KeyNotFoundException($"No FX rate configured for {from.Code}->{to.Code}.");
        }

        return rate;
    }

    public bool TryGetRate(Currency from, Currency to, DateOnly asOf, out FxRate? rate)
    {
        if (from.Code == to.Code)
        {
            rate = new FxRate(from, to, 1, 1, asOf);
            return true;
        }

        if (_rates.TryGetValue((from.Code, to.Code), out var r))
        {
            rate = new FxRate(from, to, r.Num, r.Den, asOf);
            return true;
        }

        rate = null;
        return false;
    }
}
