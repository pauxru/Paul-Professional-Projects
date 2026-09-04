using ZeroTrust.Application.Abstractions;

namespace ZeroTrust.Infrastructure.Time;

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
    public DateTimeOffset UtcNowOffset => DateTimeOffset.UtcNow;
}

public sealed class FakeClock : IClock
{
    public DateTime UtcNow { get; set; }
    public DateTimeOffset UtcNowOffset => new(UtcNow, TimeSpan.Zero);
    public FakeClock(DateTime start) { UtcNow = DateTime.SpecifyKind(start, DateTimeKind.Utc); }
    public void Advance(TimeSpan delta) { UtcNow = UtcNow.Add(delta); }
}
