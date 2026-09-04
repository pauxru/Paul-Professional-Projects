using Lakehouse.Application.Abstractions;

namespace Lakehouse.UnitTests.Support;

/// <summary>A controllable clock for deterministic time-dependent tests (freshness, valid-from stamps).</summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = start;

    public FakeClock() : this(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public void Advance(TimeSpan by) => UtcNow += by;
    public void Set(DateTimeOffset to) => UtcNow = to;
}
