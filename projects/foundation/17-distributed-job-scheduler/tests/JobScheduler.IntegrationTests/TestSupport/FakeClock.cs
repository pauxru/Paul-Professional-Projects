using JobScheduler.Application.Abstractions;

namespace JobScheduler.IntegrationTests.TestSupport;

/// <summary>Deterministic, advanceable clock so lease-expiry/retention/backoff need no real waiting.</summary>
public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private long _ticks = start.UtcTicks;

    public FakeClock() : this(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    public DateTimeOffset UtcNow => new(Interlocked.Read(ref _ticks), TimeSpan.Zero);

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public void Set(DateTimeOffset now) => Interlocked.Exchange(ref _ticks, now.UtcTicks);
}
