using JobScheduler.Application.Options;
using JobScheduler.Infrastructure.Execution;
using Microsoft.Extensions.Options;

namespace JobScheduler.UnitTests.Application;

/// <summary>
/// Per-definition circuit breaker: after N consecutive failures the circuit opens for a cooldown,
/// bounding the blast radius of one persistently failing job type so it cannot starve the fleet.
/// </summary>
public sealed class CircuitBreakerTests
{
    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static InMemoryCircuitBreaker Breaker(int threshold = 3, int cooldownSeconds = 60)
    {
        var options = Options.Create(new EngineOptions
        {
            CircuitFailureThreshold = threshold,
            CircuitCooldownSeconds = cooldownSeconds
        });
        return new InMemoryCircuitBreaker(options);
    }

    [Fact]
    public void Stays_closed_below_the_failure_threshold()
    {
        var breaker = Breaker(threshold: 3);
        var def = Guid.NewGuid();
        breaker.RecordFailure(def, T0);
        breaker.RecordFailure(def, T0);
        Assert.False(breaker.IsOpen(def, T0));
    }

    [Fact]
    public void Opens_once_the_threshold_is_reached()
    {
        var breaker = Breaker(threshold: 3, cooldownSeconds: 60);
        var def = Guid.NewGuid();
        breaker.RecordFailure(def, T0);
        breaker.RecordFailure(def, T0);
        breaker.RecordFailure(def, T0);
        Assert.True(breaker.IsOpen(def, T0.AddSeconds(30)));
    }

    [Fact]
    public void Closes_again_after_the_cooldown_elapses()
    {
        var breaker = Breaker(threshold: 1, cooldownSeconds: 60);
        var def = Guid.NewGuid();
        breaker.RecordFailure(def, T0);
        Assert.True(breaker.IsOpen(def, T0.AddSeconds(59)));
        Assert.False(breaker.IsOpen(def, T0.AddSeconds(61)));
    }

    [Fact]
    public void A_success_resets_the_failure_streak()
    {
        var breaker = Breaker(threshold: 3);
        var def = Guid.NewGuid();
        breaker.RecordFailure(def, T0);
        breaker.RecordFailure(def, T0);
        breaker.RecordSuccess(def); // streak reset
        breaker.RecordFailure(def, T0);
        Assert.False(breaker.IsOpen(def, T0));
    }

    [Fact]
    public void Breakers_are_isolated_per_definition()
    {
        var breaker = Breaker(threshold: 1);
        var failing = Guid.NewGuid();
        var healthy = Guid.NewGuid();
        breaker.RecordFailure(failing, T0);
        Assert.True(breaker.IsOpen(failing, T0));
        Assert.False(breaker.IsOpen(healthy, T0));
    }
}
