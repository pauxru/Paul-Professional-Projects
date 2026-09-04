namespace JobScheduler.Application.Abstractions;

/// <summary>
/// Business/operational metrics emitted by the engine. Implemented over OpenTelemetry
/// <c>Meter</c> instruments in the host; a no-op implementation is used in tests.
/// </summary>
public interface ISchedulerMetrics
{
    /// <summary>Latency (ms) of a successful claim from due-detection to lease acquisition.</summary>
    void RecordClaimLatency(double milliseconds);

    /// <summary>Duration (seconds) of a run, tagged by job definition and success.</summary>
    void RecordRunDuration(string jobName, double seconds, bool success);

    void SetQueueDepth(int depth);
    void SetDlqDepth(int depth);
    void RunClaimed();
    void LeaseExpired(int count = 1);
    void LeadershipChanged(string? newOwner);
}

/// <summary>Default no-op metrics sink.</summary>
public sealed class NullSchedulerMetrics : ISchedulerMetrics
{
    public static readonly NullSchedulerMetrics Instance = new();
    public void RecordClaimLatency(double milliseconds) { }
    public void RecordRunDuration(string jobName, double seconds, bool success) { }
    public void SetQueueDepth(int depth) { }
    public void SetDlqDepth(int depth) { }
    public void RunClaimed() { }
    public void LeaseExpired(int count = 1) { }
    public void LeadershipChanged(string? newOwner) { }
}

/// <summary>
/// Per-job-definition circuit breaker / retry budget. Prevents a single failing job type from
/// consuming the whole fleet's capacity: after enough consecutive failures the circuit opens and
/// that definition is skipped for a cooldown.
/// </summary>
public interface IJobCircuitBreaker
{
    bool IsOpen(Guid jobDefinitionId, DateTimeOffset now);
    void RecordSuccess(Guid jobDefinitionId);
    void RecordFailure(Guid jobDefinitionId, DateTimeOffset now);
}
