using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.UnitTests.Monetary;

public sealed class CurrencyTests
{
    [Theory]
    [InlineData("KES")]
    [InlineData("USD")]
    [InlineData("EUR")]
    public void FromCode_KnownCurrency_ReturnsSingletonWithScaleTwo(string code)
    {
        var currency = Currency.FromCode(code);

        Assert.Equal(code, currency.Code);
        Assert.Equal(2, currency.Scale);
        Assert.Equal(100, currency.MinorUnitsPerMajor);
    }

    [Fact]
    public void FromCode_UnknownCurrency_ThrowsDomainException()
    {
        var ex = Assert.Throws<DomainException>(() => Currency.FromCode("GBP"));
        Assert.Equal("ledger.unknown_currency", ex.Code);
    }

    [Fact]
    public void FromCode_IsCaseInsensitive()
    {
        Assert.Same(Currency.USD, Currency.FromCode("usd"));
    }

    [Fact]
    public void IsKnown_DistinguishesSupportedCurrencies()
    {
        Assert.True(Currency.IsKnown("EUR"));
        Assert.False(Currency.IsKnown("JPY"));
        Assert.False(Currency.IsKnown(null!));
    }
}
