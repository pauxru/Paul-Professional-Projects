using AgentPlatform.Domain.Runs;

namespace AgentPlatform.Application.Abstractions;

/// <summary>
/// Seam used only by tests to simulate a process crash at a precise point (e.g. after a mutating
/// tool has committed its side effect but before the step is marked complete). The production
/// implementation does nothing.
/// </summary>
public interface IFaultInjector
{
    Task SignalAsync(string checkpoint, WorkflowRun run, string? stepId, CancellationToken cancellationToken);
}

/// <summary>No-op fault injector used in production.</summary>
public sealed class NullFaultInjector : IFaultInjector
{
    public Task SignalAsync(string checkpoint, WorkflowRun run, string? stepId, CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>Abstraction over retry backoff delays so tests run instantly.</summary>
public interface IDelayStrategy
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class RealDelayStrategy : IDelayStrategy
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
}

/// <summary>Instant delay strategy for deterministic, fast tests.</summary>
public sealed class NoDelayStrategy : IDelayStrategy
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
}
