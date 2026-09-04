namespace NotificationPlatform.UnitTests;

using NotificationPlatform.Application.Abstractions;

public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset start) => _now = start;

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset to) => _now = to;
}
