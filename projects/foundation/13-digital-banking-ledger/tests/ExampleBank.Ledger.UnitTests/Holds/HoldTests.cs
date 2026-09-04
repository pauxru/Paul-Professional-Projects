using ExampleBank.Ledger.Domain.Common;
using ExampleBank.Ledger.Domain.Holds;
using ExampleBank.Ledger.Domain.Monetary;

namespace ExampleBank.Ledger.UnitTests.Holds;

public sealed class HoldTests
{
    private static readonly DateTimeOffset Placed = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Expiry = Placed.AddHours(24);

    private static Hold NewHold(long amountMinor = 10_000) => Hold.Create(
        Guid.NewGuid(), new Money(amountMinor, Currency.KES), Placed, Expiry, "auth-1", "idem-1");

    [Fact]
    public void Create_WithExpiryBeforePlacement_Throws()
    {
        Assert.Throws<DomainException>(() => Hold.Create(
            Guid.NewGuid(), new Money(1_000, Currency.KES), Expiry, Placed, null, null));
    }

    [Fact]
    public void CaptureFull_ReleasesEntireHeldAmount_AndMarksCaptured()
    {
        var hold = NewHold(10_000);

        var released = hold.Capture(10_000, Placed.AddHours(1));

        Assert.Equal(10_000, released); // whole original amount released from the account's held total
        Assert.Equal(HoldStatus.Captured, hold.Status);
        Assert.Equal(0, hold.RemainingMinor);
    }

    [Fact]
    public void CapturePartial_ReleasesFullHeld_AndMarksPartiallyCaptured()
    {
        var hold = NewHold(10_000);

        var released = hold.Capture(4_000, Placed.AddHours(1));

        Assert.Equal(10_000, released); // full hold released; only 4_000 becomes a posting
        Assert.Equal(HoldStatus.PartiallyCaptured, hold.Status);
        Assert.Equal(4_000, hold.CapturedMinor);
        Assert.Equal(6_000, hold.RemainingMinor);
    }

    [Fact]
    public void Capture_AmountOutsideRange_Throws()
    {
        Assert.Throws<DomainException>(() => NewHold(10_000).Capture(0, Placed));
        Assert.Throws<DomainException>(() => NewHold(10_000).Capture(10_001, Placed));
    }

    [Fact]
    public void Capture_AfterAlreadyResolved_Throws()
    {
        var hold = NewHold(10_000);
        hold.Capture(10_000, Placed.AddHours(1));

        var ex = Assert.Throws<DomainException>(() => hold.Capture(1_000, Placed.AddHours(2)));
        Assert.Equal("hold.not_active", ex.Code);
    }

    [Fact]
    public void Release_MarksReleased_AndRecordsResolution()
    {
        var hold = NewHold();

        hold.Release(Placed.AddHours(2));

        Assert.Equal(HoldStatus.Released, hold.Status);
        Assert.NotNull(hold.ResolvedAt);
    }

    [Fact]
    public void Expire_MarksExpired()
    {
        var hold = NewHold();

        hold.Expire(Expiry.AddSeconds(1));

        Assert.Equal(HoldStatus.Expired, hold.Status);
    }

    [Fact]
    public void Release_AfterCapture_Throws()
    {
        var hold = NewHold();
        hold.Capture(5_000, Placed.AddHours(1));

        Assert.Throws<DomainException>(() => hold.Release(Placed.AddHours(2)));
    }
}
