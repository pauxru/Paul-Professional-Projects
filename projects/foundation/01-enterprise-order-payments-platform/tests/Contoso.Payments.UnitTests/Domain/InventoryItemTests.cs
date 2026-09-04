using Contoso.Payments.Domain.Common;
using Contoso.Payments.Domain.Inventory;

namespace Contoso.Payments.UnitTests.Domain;

public class InventoryItemTests
{
    [Fact]
    public void Available_equals_OnHand_minus_Reserved()
    {
        var inv = new InventoryItem(Guid.NewGuid(), "SKU-1", 10);
        inv.Reserve(Guid.NewGuid(), 3);
        Assert.Equal(7, inv.Available);
        Assert.Equal(3, inv.Reserved);
    }

    [Fact]
    public void Reserve_more_than_available_throws()
    {
        var inv = new InventoryItem(Guid.NewGuid(), "SKU-1", 5);
        var ex = Assert.Throws<DomainException>(() => inv.Reserve(Guid.NewGuid(), 6));
        Assert.Equal("inventory.oversell_prevented", ex.Code);
    }

    [Fact]
    public void Release_returns_stock_to_pool()
    {
        var inv = new InventoryItem(Guid.NewGuid(), "SKU-1", 10);
        var r = inv.Reserve(Guid.NewGuid(), 4);
        inv.Release(r);
        Assert.Equal(10, inv.Available);
        Assert.Equal(0, inv.Reserved);
    }

    [Fact]
    public void Commit_deducts_from_OnHand()
    {
        var inv = new InventoryItem(Guid.NewGuid(), "SKU-1", 10);
        var r = inv.Reserve(Guid.NewGuid(), 4);
        inv.Commit(r);
        Assert.Equal(6, inv.OnHand);
        Assert.Equal(6, inv.Available);
    }

    [Fact]
    public void Reserve_zero_or_negative_throws()
    {
        var inv = new InventoryItem(Guid.NewGuid(), "SKU-1", 10);
        Assert.Throws<DomainException>(() => inv.Reserve(Guid.NewGuid(), 0));
        Assert.Throws<DomainException>(() => inv.Reserve(Guid.NewGuid(), -1));
    }

    [Fact]
    public void Release_missing_reservation_throws()
    {
        var inv = new InventoryItem(Guid.NewGuid(), "SKU-1", 10);
        Assert.Throws<DomainException>(() => inv.Release(Guid.NewGuid()));
    }

    [Fact]
    public void Committing_a_released_reservation_throws()
    {
        var inv = new InventoryItem(Guid.NewGuid(), "SKU-1", 10);
        var r = inv.Reserve(Guid.NewGuid(), 2);
        inv.Release(r);
        Assert.Throws<DomainException>(() => inv.Commit(r));
    }

    [Fact]
    public void Version_increments_on_state_change()
    {
        var inv = new InventoryItem(Guid.NewGuid(), "SKU-1", 10);
        var before = inv.Version;
        inv.Reserve(Guid.NewGuid(), 1);
        Assert.True(inv.Version > before);
    }
}
