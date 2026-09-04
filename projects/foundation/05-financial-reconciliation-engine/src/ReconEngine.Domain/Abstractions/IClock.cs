namespace ReconEngine.Domain.Abstractions;

/// <summary>
/// Abstraction over the system clock so that time-dependent logic is deterministic in tests.
/// Domain and application code must never call <see cref="System.DateTime.UtcNow"/> directly.
/// </summary>
public interface IClock
{
    DateTime UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
