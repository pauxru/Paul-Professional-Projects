namespace JobScheduler.Application.Abstractions;

/// <summary>
/// Abstraction over the system clock. Domain and application code depend on this rather than
/// <see cref="DateTimeOffset.UtcNow"/> so time can be controlled deterministically in tests.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}
