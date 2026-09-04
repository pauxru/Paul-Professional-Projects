using Contoso.Storefront.Domain;

namespace Contoso.Storefront.UnitTests;

public sealed class DomainTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Money_WithNegativeAmount_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Money(-0.01m, "USD"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("US")]
    [InlineData("USDD")]
    public void Money_WithInvalidCurrency_Throws(string currency)
    {
        Assert.Throws<ArgumentException>(() => new Money(1, currency));
    }

    [Fact]
    public void Money_AddingDifferentCurrencies_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => _ = new Money(1, "USD") + new Money(1, "KES"));
    }

    [Fact]
    public void Product_Constructor_NormalizesSkuAndCurrency()
    {
        var product = new Product(
            Guid.NewGuid(),
            " demo-1 ",
            "Demo",
            "Description",
            new Money(12.345m, "usd"),
            Now);

        Assert.Equal("DEMO-1", product.Sku);
        Assert.Equal(12.35m, product.PriceAmount);
        Assert.Equal("USD", product.Currency);
    }

    [Fact]
    public void Order_SubmitWithoutItems_Throws()
    {
        var order = NewOrder();

        Assert.Throws<InvalidOperationException>(order.Submit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Order_AddItemOutsideQuantityRange_Throws(int quantity)
    {
        var order = NewOrder();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => order.AddItem(Guid.NewGuid(), "Product", quantity, new Money(2, "USD")));
    }

    [Fact]
    public void Order_AddingMixedCurrencies_Throws()
    {
        var order = NewOrder();
        order.AddItem(Guid.NewGuid(), "Product", 1, new Money(2, "USD"));

        Assert.Throws<InvalidOperationException>(
            () => order.AddItem(Guid.NewGuid(), "Other", 1, new Money(100, "KES")));
    }

    [Fact]
    public void Order_WithItems_ComputesTotalAndLocksAfterSubmission()
    {
        var order = NewOrder();
        order.AddItem(Guid.NewGuid(), "Coffee", 2, new Money(18.50m, "USD"));
        order.AddItem(Guid.NewGuid(), "Mug", 1, new Money(12, "USD"));

        order.Submit();

        Assert.Equal(49m, order.Total.Amount);
        Assert.Equal(OrderStatus.Submitted, order.Status);
        Assert.Throws<InvalidOperationException>(
            () => order.AddItem(Guid.NewGuid(), "Late item", 1, new Money(1, "USD")));
    }

    private static Order NewOrder() =>
        new(Guid.NewGuid(), "customer-1", "idem-1", Now);
}
