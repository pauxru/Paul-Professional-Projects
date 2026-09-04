using Contoso.Payments.Domain.Common;

namespace Contoso.Payments.UnitTests.Domain;

public class MoneyTests
{
    [Fact]
    public void Of_normalises_currency_case()
    {
        var m = Money.Of(10m, "usd");
        Assert.Equal("USD", m.Currency);
    }

    [Fact]
    public void Rejects_unsupported_currency()
    {
        var ex = Assert.Throws<DomainException>(() => Money.Of(10m, "EUR"));
        Assert.Contains("EUR", ex.Message);
    }

    [Fact]
    public void Add_of_same_currency_sums()
    {
        var sum = Money.Of(2m, "USD").Add(Money.Of(3m, "USD"));
        Assert.Equal(5m, sum.Amount);
    }

    [Fact]
    public void Add_of_different_currency_throws()
    {
        Assert.Throws<DomainException>(() => Money.Of(2m, "USD").Add(Money.Of(3m, "KES")));
    }

    [Fact]
    public void Multiply_by_quantity()
    {
        var total = Money.Of(2.5m, "USD").Multiply(4);
        Assert.Equal(10m, total.Amount);
    }

    [Fact]
    public void Multiply_by_negative_throws()
    {
        Assert.Throws<DomainException>(() => Money.Of(2m, "USD").Multiply(-1));
    }

    [Fact]
    public void Subtract_reduces_amount()
    {
        var diff = Money.Of(10m, "USD").Subtract(Money.Of(3.5m, "USD"));
        Assert.Equal(6.5m, diff.Amount);
    }

    [Fact]
    public void To_and_from_minor_units_round_trip()
    {
        var m = Money.Of(12.34m, "USD");
        Assert.Equal(1234L, m.ToMinorUnits());
        var back = Money.FromMinorUnits(1234, "USD");
        Assert.Equal(m.Amount, back.Amount);
        Assert.Equal(m.Currency, back.Currency);
    }

    [Fact]
    public void GreaterThan_across_currencies_throws()
    {
        Assert.Throws<DomainException>(() => Money.Of(5m, "USD").GreaterThan(Money.Of(3m, "KES")));
    }
}
