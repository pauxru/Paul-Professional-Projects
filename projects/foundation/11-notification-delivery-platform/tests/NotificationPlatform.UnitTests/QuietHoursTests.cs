namespace NotificationPlatform.UnitTests;

using NotificationPlatform.Application.Fairness;
using NotificationPlatform.Domain.Common;
using Xunit;

public sealed class QuietHoursTests
{
    // Nairobi is UTC+3, no DST. Alice-like recipient with quiet hours 22:00-06:00 local.
    [Fact]
    public void DefersToNextMorningWhenInsideCrossMidnightWindow()
    {
        var calc = new QuietHoursCalculator();
        // 23:30 local Nairobi = 20:30 UTC
        var now = new DateTimeOffset(2026, 09, 03, 20, 30, 0, TimeSpan.Zero);
        var next = calc.ComputeDeferralTarget(now, "Africa/Nairobi", TimeSpan.FromHours(22), TimeSpan.FromHours(6), NotificationPriority.Marketing);
        // Expected: next 06:00 Nairobi local, which is 03:00 UTC on 2026-09-04
        Assert.Equal(new DateTimeOffset(2026, 09, 04, 6, 0, 0, TimeSpan.FromHours(3)), next);
    }

    [Fact]
    public void DoesNotDeferOutsideQuietWindow()
    {
        var calc = new QuietHoursCalculator();
        var now = new DateTimeOffset(2026, 09, 03, 12, 0, 0, TimeSpan.Zero); // 15:00 Nairobi
        var next = calc.ComputeDeferralTarget(now, "Africa/Nairobi", TimeSpan.FromHours(22), TimeSpan.FromHours(6), NotificationPriority.Marketing);
        Assert.Equal(now, next);
    }

    [Fact]
    public void TransactionalBypassesQuietHours()
    {
        var calc = new QuietHoursCalculator();
        var now = new DateTimeOffset(2026, 09, 03, 20, 30, 0, TimeSpan.Zero);
        var next = calc.ComputeDeferralTarget(now, "Africa/Nairobi", TimeSpan.FromHours(22), TimeSpan.FromHours(6), NotificationPriority.Transactional);
        Assert.Equal(now, next);
    }

    [Fact]
    public void SameStartAndEndMeansNoQuietHours()
    {
        var calc = new QuietHoursCalculator();
        var now = new DateTimeOffset(2026, 09, 03, 3, 0, 0, TimeSpan.Zero);
        var next = calc.ComputeDeferralTarget(now, "Africa/Nairobi", TimeSpan.Zero, TimeSpan.Zero, NotificationPriority.Marketing);
        Assert.Equal(now, next);
    }

    [Fact]
    public void HandlesWindowThatDoesNotCrossMidnight()
    {
        var calc = new QuietHoursCalculator();
        // 09:30 UTC = 12:30 Nairobi, inside 10:00-14:00 local window
        var now = new DateTimeOffset(2026, 09, 03, 9, 30, 0, TimeSpan.Zero);
        var next = calc.ComputeDeferralTarget(now, "Africa/Nairobi", TimeSpan.FromHours(10), TimeSpan.FromHours(14), NotificationPriority.Marketing);
        Assert.Equal(new DateTimeOffset(2026, 09, 03, 14, 0, 0, TimeSpan.FromHours(3)), next);
    }
}
