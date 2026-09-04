using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.UnitTests.Monetary;

public sealed class MoneyTests
{
    [Fact]
    public void Add_SameCurrency_SumsMinorUnits()
    {
        var a = new Money(1500, Currency.KES);
        var b = new Money(2500, Currency.KES);

        Assert.Equal(4000, a.Add(b).MinorUnits);
    }

    [Fact]
    public void Subtract_SameCurrency_SubtractsMinorUnits()
    {
        var a = new Money(1500, Currency.USD);
        var b = new Money(500, Currency.USD);

        Assert.Equal(1000, a.Subtract(b).MinorUnits);
    }

    [Fact]
    public void Add_DifferentCurrencies_ThrowsMixedCurrency()
    {
        var kes = new Money(1000, Currency.KES);
        var usd = new Money(1000, Currency.USD);

        Assert.Throws<MixedCurrencyException>(() => kes.Add(usd));
    }

    [Fact]
    public void Subtract_DifferentCurrencies_ThrowsMixedCurrency()
    {
        var kes = new Money(1000, Currency.KES);
        var usd = new Money(1000, Currency.USD);

        Assert.Throws<MixedCurrencyException>(() => kes.Subtract(usd));
    }

    [Fact]
    public void FromMajor_WholeMinorUnits_ConvertsExactly()
    {
        var money = Money.FromMajor(123.45m, Currency.USD);

        Assert.Equal(12345, money.MinorUnits);
        Assert.Equal(123.45m, money.ToMajor());
    }

    [Fact]
    public void FromMajor_SubMinorPrecision_ThrowsDomainException()
    {
        var ex = Assert.Throws<DomainException>(() => Money.FromMajor(1.234m, Currency.EUR));
        Assert.Equal("ledger.sub_minor_precision", ex.Code);
    }

    [Fact]
    public void Negate_FlipsSign_AndKeepsCurrency()
    {
        var money = new Money(750, Currency.EUR);
        var negated = money.Negate();

        Assert.Equal(-750, negated.MinorUnits);
        Assert.Equal(Currency.EUR, negated.Currency);
    }

    [Fact]
    public void Sign_Predicates_ReflectMinorUnits()
    {
        Assert.True(new Money(0, Currency.KES).IsZero);
        Assert.True(new Money(1, Currency.KES).IsPositive);
        Assert.True(new Money(-1, Currency.KES).IsNegative);
    }
}
