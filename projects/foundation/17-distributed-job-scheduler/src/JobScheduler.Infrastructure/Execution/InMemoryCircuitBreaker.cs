using System.Collections.Concurrent;
using JobScheduler.Application.Abstractions;
using JobScheduler.Application.Options;
using Microsoft.Extensions.Options;

namespace JobScheduler.Infrastructure.Execution;

/// <summary>
/// Thread-safe, in-memory per-definition circuit breaker. After a configurable number of
/// consecutive failures a definition's circuit opens for a cooldown, during which the engine skips
/// claiming its runs. This bounds the blast radius of one persistently failing job type so it
/// cannot monopolise the worker fleet (retry-budget / bulkhead behaviour).
/// </summary>
public sealed class InMemoryCircuitBreaker(IOptions<EngineOptions> options) : IJobCircuitBreaker
{
    private sealed class State
    {
        public int ConsecutiveFailures;
        public DateTimeOffset OpenUntil;
    }

    private readonly ConcurrentDictionary<Guid, State> _states = new();
    private readonly EngineOptions _options = options.Value;

    public bool IsOpen(Guid jobDefinitionId, DateTimeOffset now) =>
        _states.TryGetValue(jobDefinitionId, out var s) && s.OpenUntil > now;

    public void RecordSuccess(Guid jobDefinitionId)
    {
        if (_states.TryGetValue(jobDefinitionId, out var s))
        {
            Interlocked.Exchange(ref s.ConsecutiveFailures, 0);
            s.OpenUntil = DateTimeOffset.MinValue;
        }
    }

    public void RecordFailure(Guid jobDefinitionId, DateTimeOffset now)
    {
        var s = _states.GetOrAdd(jobDefinitionId, _ => new State());
        int failures = Interlocked.Increment(ref s.ConsecutiveFailures);
        if (failures >= _options.CircuitFailureThreshold)
        {
            s.OpenUntil = now + _options.CircuitCooldown;
        }
    }
}
