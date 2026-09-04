namespace Idp.Application.Abstractions;

/// <summary>Abstraction over the system clock so time-dependent logic is deterministic in tests.</summary>
public interface IClock
{
    DateTime UtcNow { get; }
}
