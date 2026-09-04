using ExampleBank.Ledger.Application.Abstractions;

namespace ExampleBank.Ledger.UnitTests.TestSupport;

/// <summary>
/// A deterministic <see cref="IClock"/> test double. Time never advances on its own; tests move it
/// explicitly with <see cref="Advance"/> or <see cref="Set"/> so temporal behaviour is reproducible.
/// </summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset now) => UtcNow = now;

    public FakeClock(int year, int month, int day)
        : this(new DateTimeOffset(year, month, day, 0, 0, 0, TimeSpan.Zero))
    {
    }

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);

    public void Set(DateTimeOffset now) => UtcNow = now;
}
