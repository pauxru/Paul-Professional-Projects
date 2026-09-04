using ReconEngine.Domain.Abstractions;

namespace ReconEngine.UnitTests.TestKit;

/// <summary>A controllable clock for deterministic workflow/aging tests.</summary>
public sealed class TestClock : IClock
{
    public TestClock(DateTime? start = null) =>
        UtcNow = start ?? new DateTime(2024, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow { get; private set; }

    public void Advance(TimeSpan by) => UtcNow = UtcNow.Add(by);
}
