using Northstar.Reliability.Application.Abstractions;

namespace Northstar.Reliability.UnitTests.Fixtures;

public sealed class FakeClock(DateTimeOffset initial) : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = initial;

    public void Advance(TimeSpan duration) => UtcNow = UtcNow.Add(duration);

    public void Set(DateTimeOffset value) => UtcNow = value;
}
