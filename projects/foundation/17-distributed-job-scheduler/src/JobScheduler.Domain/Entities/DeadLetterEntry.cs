namespace JobScheduler.Domain.Entities;

/// <summary>
/// A run that exhausted its retry budget (or was classified poison) and was parked for human
/// inspection. Replay re-arms the original run as a fresh Pending instance.
/// </summary>
public sealed class DeadLetterEntry
{
    private DeadLetterEntry() { }

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid JobRunId { get; private set; }
    public Guid JobDefinitionId { get; private set; }
    public string JobName { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string? Error { get; private set; }
    public string PayloadJson { get; private set; } = "{}";
    public int AttemptCount { get; private set; }
    public DateTimeOffset DeadLetteredAt { get; private set; }
    public bool Replayed { get; private set; }
    public Guid? ReplayedRunId { get; private set; }
    public DateTimeOffset? ReplayedAt { get; private set; }

    public static DeadLetterEntry FromRun(JobRun run, string reason, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        JobRunId = run.Id,
        JobDefinitionId = run.JobDefinitionId,
        JobName = run.JobName,
        Reason = reason,
        Error = run.Error,
        PayloadJson = run.PayloadJson,
        AttemptCount = run.AttemptCount,
        DeadLetteredAt = now
    };

    public void MarkReplayed(Guid replayedRunId, DateTimeOffset now)
    {
        Replayed = true;
        ReplayedRunId = replayedRunId;
        ReplayedAt = now;
    }
}

/// <summary>Structured, append-only log line attached to a run and stamped with its correlation id.</summary>
public sealed class RunLog
{
    private RunLog() { }

    public long Id { get; private set; }
    public Guid JobRunId { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public string Level { get; private set; } = "Information";
    public string Message { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;
    public string? NodeId { get; private set; }
    public int Attempt { get; private set; }

    public static RunLog For(
        Guid runId,
        string level,
        string message,
        string correlationId,
        string? nodeId,
        int attempt,
        DateTimeOffset now) => new()
        {
            JobRunId = runId,
            Level = level,
            Message = message,
            CorrelationId = correlationId,
            NodeId = nodeId,
            Attempt = attempt,
            Timestamp = now
        };
}
