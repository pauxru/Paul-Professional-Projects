using AgentPlatform.Domain.Abstractions;

namespace AgentPlatform.UnitTests.TestSupport;

/// <summary>A controllable clock for deterministic time-dependent tests.</summary>
public sealed class FakeClock : IClock
{
    public FakeClock(DateTimeOffset? start = null) => UtcNow = start ?? new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public DateTimeOffset UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
