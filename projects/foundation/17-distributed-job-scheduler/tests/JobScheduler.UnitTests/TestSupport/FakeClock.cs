using JobScheduler.Application.Abstractions;

namespace JobScheduler.UnitTests.TestSupport;

/// <summary>
/// Deterministic clock for tests. Time only advances when a test advances it, so retention,
/// lease-expiry, backoff and aging behaviour can be asserted without real waiting.
/// </summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public FakeClock() : this(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan by) => _now += by;

    public void Set(DateTimeOffset now) => _now = now;
}
