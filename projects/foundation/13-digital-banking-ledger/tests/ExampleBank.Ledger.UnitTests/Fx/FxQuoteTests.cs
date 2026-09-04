using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Application.Fx;
using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.UnitTests.Fx;

public sealed class FxQuoteTests
{
    private static readonly DateOnly AsOf = new(2024, 1, 1);

    [Fact]
    public void Quote_ExactRate_ProducesExactTargetWithNoRemainder()
    {
        // USD -> KES at 130/1: 100.00 USD (10,000 minor) => 13,000.00 KES (1,300,000 minor).
        var rate = new FxRate(Currency.USD, Currency.KES, 130, 1, AsOf);

        var quote = FxService.Quote(Currency.USD, Currency.KES, 10_000, rate);

        Assert.Equal(1_300_000, quote.TargetMinor);
        Assert.Equal(0, quote.RemainderNumerator);
    }

    [Fact]
    public void Quote_InexactRate_ConservesValueExactlyViaRemainder()
    {
        // EUR -> USD at 108/100: 3.33 EUR (333 minor) => 3.59 USD, with a conserved remainder.
        var rate = new FxRate(Currency.EUR, Currency.USD, 108, 100, AsOf);

        var quote = FxService.Quote(Currency.EUR, Currency.USD, 333, rate);

        Assert.Equal(359, quote.TargetMinor);
        Assert.Equal(6_400, quote.RemainderNumerator);
        Assert.Equal(10_000, quote.RemainderDenominator);

        // The headline invariant: no value is created or destroyed by conversion + rounding.
        long lhs = quote.TargetMinor * quote.RemainderDenominator + quote.RemainderNumerator;
        long rhs = (long)quote.SourceMinor * rate.Numerator * Currency.USD.MinorUnitsPerMajor;
        Assert.Equal(rhs, lhs);
    }

    [Fact]
    public void Quote_RemainderIsAlwaysLessThanDenominator()
    {
        var rate = new FxRate(Currency.EUR, Currency.USD, 108, 100, AsOf);

        var quote = FxService.Quote(Currency.EUR, Currency.USD, 777, rate);

        Assert.True(quote.RemainderNumerator >= 0);
        Assert.True(quote.RemainderNumerator < quote.RemainderDenominator);
    }

    [Fact]
    public void Quote_NonPositiveAmount_Throws()
    {
        var rate = new FxRate(Currency.USD, Currency.KES, 130, 1, AsOf);

        Assert.Throws<DomainException>(() => FxService.Quote(Currency.USD, Currency.KES, 0, rate));
    }
}
