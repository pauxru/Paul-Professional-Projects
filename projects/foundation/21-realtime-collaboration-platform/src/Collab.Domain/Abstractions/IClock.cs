namespace Collab.Domain.Abstractions;

/// <summary>
/// Wall-clock abstraction. Domain and application code must never call
/// <see cref="System.DateTime.UtcNow"/> directly so that time is controllable in tests.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock backed by the system time.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Deterministic clock for tests. Advances only when told to.</summary>
public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;
    public FakeClock(DateTimeOffset start) => _now = start;
    public FakeClock() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public DateTimeOffset UtcNow => _now;
    public void Advance(TimeSpan by) => _now = _now.Add(by);
    public void Set(DateTimeOffset to) => _now = to;
}
