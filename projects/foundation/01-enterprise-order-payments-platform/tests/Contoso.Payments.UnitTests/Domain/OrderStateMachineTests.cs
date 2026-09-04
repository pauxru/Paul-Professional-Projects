using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Orders;

namespace Contoso.Payments.UnitTests.Domain;

public class OrderStateMachineTests
{
    private static Order NewDraftWithLine()
    {
        var order = new Order(Guid.NewGuid(), "customer-1", "USD", DateTimeOffset.UtcNow);
        order.AddLine(Guid.NewGuid(), "SKU-1", 1, Money.Of(10m, "USD"));
        return order;
    }

    [Fact]
    public void NewOrder_starts_in_Draft()
    {
        var o = new Order(Guid.NewGuid(), "c", "USD", DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.Draft, o.Status);
    }

    [Fact]
    public void MarkPending_moves_Draft_to_Pending()
    {
        var o = NewDraftWithLine();
        o.MarkPending(DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.Pending, o.Status);
    }

    [Fact]
    public void MarkPending_on_empty_order_throws()
    {
        var o = new Order(Guid.NewGuid(), "c", "USD", DateTimeOffset.UtcNow);
        var ex = Assert.Throws<DomainException>(() => o.MarkPending(DateTimeOffset.UtcNow));
        Assert.Equal("order.empty", ex.Code);
    }

    [Fact]
    public void MoveToAwaitingPayment_only_valid_from_Pending()
    {
        var o = NewDraftWithLine();
        o.MarkPending(DateTimeOffset.UtcNow);
        o.MoveToAwaitingPayment(Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.AwaitingPayment, o.Status);
    }

    [Fact]
    public void MoveToAwaitingPayment_from_Draft_throws()
    {
        var o = NewDraftWithLine();
        var ex = Assert.Throws<DomainException>(() => o.MoveToAwaitingPayment(Guid.NewGuid(), DateTimeOffset.UtcNow));
        Assert.Equal("order.illegal_transition", ex.Code);
    }

    [Fact]
    public void MarkPaid_only_valid_from_AwaitingPayment()
    {
        var o = NewDraftWithLine();
        o.MarkPending(DateTimeOffset.UtcNow);
        o.MoveToAwaitingPayment(Guid.NewGuid(), DateTimeOffset.UtcNow);
        o.MarkPaid(DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.Paid, o.Status);
    }

    [Fact]
    public void MarkFulfilled_only_valid_from_Paid()
    {
        var o = NewDraftWithLine();
        o.MarkPending(DateTimeOffset.UtcNow);
        o.MoveToAwaitingPayment(Guid.NewGuid(), DateTimeOffset.UtcNow);
        o.MarkPaid(DateTimeOffset.UtcNow);
        o.MarkFulfilled(DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.Fulfilled, o.Status);
    }

    [Fact]
    public void MarkFulfilled_from_Pending_throws()
    {
        var o = NewDraftWithLine();
        o.MarkPending(DateTimeOffset.UtcNow);
        Assert.Throws<DomainException>(() => o.MarkFulfilled(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Cancel_valid_from_Draft_Pending_AwaitingPayment_and_Paid()
    {
        foreach (var stage in new[] { 0, 1, 2, 3 })
        {
            var o = NewDraftWithLine();
            if (stage >= 1) o.MarkPending(DateTimeOffset.UtcNow);
            if (stage >= 2) o.MoveToAwaitingPayment(Guid.NewGuid(), DateTimeOffset.UtcNow);
            if (stage >= 3) o.MarkPaid(DateTimeOffset.UtcNow);
            o.Cancel(DateTimeOffset.UtcNow);
            Assert.Equal(OrderStatus.Cancelled, o.Status);
        }
    }

    [Fact]
    public void Cancel_from_Fulfilled_throws()
    {
        var o = NewDraftWithLine();
        o.MarkPending(DateTimeOffset.UtcNow);
        o.MoveToAwaitingPayment(Guid.NewGuid(), DateTimeOffset.UtcNow);
        o.MarkPaid(DateTimeOffset.UtcNow);
        o.MarkFulfilled(DateTimeOffset.UtcNow);
        var ex = Assert.Throws<DomainException>(() => o.Cancel(DateTimeOffset.UtcNow));
        Assert.Equal("order.illegal_transition", ex.Code);
    }

    [Fact]
    public void Cancel_is_idempotent()
    {
        var o = NewDraftWithLine();
        o.Cancel(DateTimeOffset.UtcNow);
        o.Cancel(DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.Cancelled, o.Status);
    }

    [Fact]
    public void Partial_refund_moves_to_PartiallyRefunded()
    {
        var o = NewDraftWithLine();
        o.MarkPending(DateTimeOffset.UtcNow);
        o.MoveToAwaitingPayment(Guid.NewGuid(), DateTimeOffset.UtcNow);
        o.MarkPaid(DateTimeOffset.UtcNow);
        o.RecordRefund(Money.Of(3m, "USD"), DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.PartiallyRefunded, o.Status);
        Assert.Equal(3m, o.RefundedTotal.Amount);
    }

    [Fact]
    public void Full_refund_moves_to_Refunded()
    {
        var o = NewDraftWithLine();
        o.MarkPending(DateTimeOffset.UtcNow);
        o.MoveToAwaitingPayment(Guid.NewGuid(), DateTimeOffset.UtcNow);
        o.MarkPaid(DateTimeOffset.UtcNow);
        o.RecordRefund(Money.Of(10m, "USD"), DateTimeOffset.UtcNow);
        Assert.Equal(OrderStatus.Refunded, o.Status);
    }

    [Fact]
    public void Refund_exceeding_total_throws()
    {
        var o = NewDraftWithLine();
        o.MarkPending(DateTimeOffset.UtcNow);
        o.MoveToAwaitingPayment(Guid.NewGuid(), DateTimeOffset.UtcNow);
        o.MarkPaid(DateTimeOffset.UtcNow);
        var ex = Assert.Throws<DomainException>(() => o.RecordRefund(Money.Of(999m, "USD"), DateTimeOffset.UtcNow));
        Assert.Equal("refund.exceeds_captured", ex.Code);
    }
}
