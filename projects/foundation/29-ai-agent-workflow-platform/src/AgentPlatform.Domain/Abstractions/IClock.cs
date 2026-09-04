namespace AgentPlatform.Domain.Abstractions;

/// <summary>
/// Abstraction over the system clock. Domain and application code never call
/// <see cref="DateTimeOffset.UtcNow"/> directly so that time-dependent behaviour
/// (timeouts, backoff, approval expiry) is deterministic under test.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock backed by the operating system.</summary>
public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
