namespace Healthcare.Application.Abstractions;

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;
    public FakeClock(DateTimeOffset now) => _now = now;
    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
    public void Set(DateTimeOffset now) => _now = now;
}
