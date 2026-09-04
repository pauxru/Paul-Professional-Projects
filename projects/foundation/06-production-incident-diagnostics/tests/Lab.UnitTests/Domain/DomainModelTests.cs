using Lab.Domain.Entities;

namespace Lab.UnitTests.Domain;

public sealed class DomainModelTests
{
    [Fact]
    public void Customer_EmptyName_IsRejected()
    {
        Assert.Throws<ArgumentException>(() => new Customer("", "operations@example.test"));
    }

    [Fact]
    public void Customer_NormalizesEmail()
    {
        var customer = new Customer("Northstar Logistics (fictional)", "OPERATIONS@EXAMPLE.TEST ");

        Assert.Equal("operations@example.test", customer.Email);
    }

    [Fact]
    public void Order_ValidInput_StartsBooked()
    {
        var order = CreateOrder();

        Assert.Equal(OrderStatus.Booked, order.Status);
        Assert.Equal("NS-20001", order.Reference);
    }

    [Fact]
    public void Order_Dispatch_TransitionsToInTransit()
    {
        var order = CreateOrder();
        order.Dispatch("trk-20001", DateTimeOffset.Parse("2026-01-02T00:00:00Z"));

        Assert.Equal(OrderStatus.InTransit, order.Status);
        Assert.Equal("TRK-20001", order.Shipment!.TrackingNumber);
    }

    [Fact]
    public void Order_DoubleDispatch_IsRejected()
    {
        var order = CreateOrder();
        order.Dispatch("TRK-20001", DateTimeOffset.Parse("2026-01-02T00:00:00Z"));

        Assert.Throws<InvalidOperationException>(() => order.Dispatch("TRK-20002", DateTimeOffset.Parse("2026-01-03T00:00:00Z")));
    }

    [Fact]
    public void Shipment_DeliveryBeforeDispatch_IsRejected()
    {
        var shipment = new Shipment(Guid.NewGuid(), "TRK-20001", DateTimeOffset.Parse("2026-01-02T00:00:00Z"));

        Assert.Throws<ArgumentException>(() => shipment.MarkDelivered(DateTimeOffset.Parse("2026-01-01T00:00:00Z")));
    }

    [Fact]
    public void Order_Delivery_TransitionsToDelivered()
    {
        var order = CreateOrder();
        order.Dispatch("TRK-20001", DateTimeOffset.Parse("2026-01-02T00:00:00Z"));
        order.MarkDelivered(DateTimeOffset.Parse("2026-01-03T00:00:00Z"));

        Assert.Equal(OrderStatus.Delivered, order.Status);
        Assert.NotNull(order.Shipment!.DeliveredAt);
    }

    private static LogisticsOrder CreateOrder() =>
        new(Guid.NewGuid(), "ns-20001", "Nairobi distribution centre", DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
}
