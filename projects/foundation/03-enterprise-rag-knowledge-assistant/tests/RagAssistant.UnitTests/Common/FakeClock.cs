using RagAssistant.Domain.Common;

namespace RagAssistant.UnitTests.Common;

public sealed class FakeClock : IClock
{
    private DateTimeOffset _now;

    public FakeClock(DateTimeOffset start)
    {
        _now = start;
    }

    public DateTimeOffset UtcNow => _now;

    public void Advance(TimeSpan duration)
    {
        _now = _now.Add(duration);
    }
}
