using System.Diagnostics;

namespace LoadRunner.Core.Time;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    long GetTimestamp();

    TimeSpan GetElapsed(long startTimestamp) => Stopwatch.GetElapsedTime(startTimestamp, GetTimestamp());
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long GetTimestamp() => Stopwatch.GetTimestamp();
}

public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;
    private long _timestamp;

    public FakeClock(DateTimeOffset initial)
    {
        _now = initial;
        _timestamp = 0;
    }

    public DateTimeOffset UtcNow => _now;
    public long GetTimestamp() => _timestamp;

    public void Advance(TimeSpan span)
    {
        _now = _now.Add(span);
        _timestamp += (long)(span.TotalSeconds * Stopwatch.Frequency);
    }
}
