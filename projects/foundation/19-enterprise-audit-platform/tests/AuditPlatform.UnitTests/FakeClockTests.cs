using AuditPlatform.Domain.Time;
using Xunit;

namespace AuditPlatform.UnitTests;

public class FakeClockTests
{
    [Fact]
    public void FakeClock_Advances()
    {
        var seed = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var clock = new FakeClock(seed);
        Assert.Equal(seed, clock.UtcNow);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(seed.AddMinutes(5), clock.UtcNow);
    }

    [Fact]
    public void FakeClock_CanSet()
    {
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var target = new DateTimeOffset(2030, 12, 31, 23, 59, 59, TimeSpan.Zero);
        clock.Set(target);
        Assert.Equal(target, clock.UtcNow);
    }
}
