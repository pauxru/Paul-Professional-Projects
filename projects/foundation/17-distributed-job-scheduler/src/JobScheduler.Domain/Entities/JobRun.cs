namespace JobScheduler.Domain.Entities;

/// <summary>
/// A single execution instance of a job definition. Owns the lease fields
/// (<see cref="LeaseOwner"/>, <see cref="LeaseToken"/>, <see cref="LeaseExpiresAt"/>) and the
/// monotonic <see cref="FencingToken"/> used to reject writes from a stalled, superseded worker.
/// All state changes go through methods that consult <see cref="RunStateMachine"/>.
/// </summary>
public sealed class JobRun
{
    private JobRun() { } // EF

    public Guid Id { get; private set; } = Guid.NewGuid();
    public Guid JobDefinitionId { get; private set; }
    public string JobName { get; private set; } = string.Empty;
    public string HandlerType { get; private set; } = string.Empty;
    public string PayloadJson { get; private set; } = "{}";
    public string Queue { get; private set; } = "default";
    public int Priority { get; private set; }

    public RunState State { get; private set; } = RunState.Pending;
    public int AttemptCount { get; private set; }
    public int MaxAttempts { get; private set; } = 3;

    public DateTimeOffset ScheduledAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? FinishedAt { get; private set; }

    // Lease / fencing
    public string? LeaseOwner { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseExpiresAt { get; private set; }
    public long FencingToken { get; private set; }

    // Idempotency & tracing
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string CorrelationId { get; private set; } = string.Empty;
    public string TriggerKind { get; private set; } = "manual";
    public string? DependencyKey { get; private set; }

    public string? Output { get; private set; }
    public string? Error { get; private set; }

    /// <summary>Set by the cancel API; observed by the executing worker for cooperative cancellation.</summary>
    public bool CancelRequested { get; private set; }

    /// <summary>Optimistic-concurrency guard used by the store for non-atomic transitions.</summary>
    public int Version { get; private set; }

    public static JobRun Create(
        JobDefinition definition,
        DateTimeOffset scheduledAt,
        DateTimeOffset now,
        string idempotencyKey,
        string correlationId,
        string triggerKind,
        string? payloadJsonOverride = null)
    {
        return new JobRun
        {
            Id = Guid.NewGuid(),
            JobDefinitionId = definition.Id,
            JobName = definition.Name,
            HandlerType = definition.HandlerType,
            PayloadJson = string.IsNullOrWhiteSpace(payloadJsonOverride) ? definition.PayloadJson : payloadJsonOverride,
            Queue = definition.Queue,
            Priority = definition.Priority,
            MaxAttempts = definition.MaxAttempts,
            State = RunState.Pending,
            ScheduledAt = scheduledAt,
            CreatedAt = now,
            IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey,
            CorrelationId = string.IsNullOrWhiteSpace(correlationId) ? Guid.NewGuid().ToString("N") : correlationId,
            TriggerKind = triggerKind
        };
    }

    public bool IsLeaseExpired(DateTimeOffset now) =>
        LeaseExpiresAt is not null && LeaseExpiresAt <= now;

    /// <summary>
    /// Claims the run for a worker. The presented <paramref name="fencingToken"/> must be greater
    /// than the current one (monotonicity), guaranteeing a later claim always supersedes an earlier.
    /// </summary>
    public void Claim(string nodeId, Guid leaseToken, long fencingToken, DateTimeOffset leaseExpiresAt)
    {
        RunStateMachine.EnsureTransition(State, RunState.Claimed);
        if (fencingToken <= FencingToken)
        {
            throw new InvalidOperationException(
                $"Fencing token must be monotonic: {fencingToken} <= current {FencingToken}.");
        }

        State = RunState.Claimed;
        LeaseOwner = Guard.NotBlank(nodeId, nameof(nodeId));
        LeaseToken = leaseToken;
        FencingToken = fencingToken;
        LeaseExpiresAt = leaseExpiresAt;
        Version++;
    }

    public void BeginRunning(DateTimeOffset now)
    {
        RunStateMachine.EnsureTransition(State, RunState.Running);
        State = RunState.Running;
        StartedAt = now;
        AttemptCount++;
        Version++;
    }

    /// <summary>Extends the lease if the presented token owns it. Returns false otherwise.</summary>
    public bool Heartbeat(Guid leaseToken, DateTimeOffset newExpiry)
    {
        if (!RunStateMachine.IsActive(State) || LeaseToken != leaseToken)
        {
            return false;
        }
        LeaseExpiresAt = newExpiry;
        return true;
    }

    public void Succeed(long fencingToken, string? output, DateTimeOffset now)
    {
        EnsureFencing(fencingToken);
        RunStateMachine.EnsureTransition(State, RunState.Succeeded);
        State = RunState.Succeeded;
        Output = output;
        FinishedAt = now;
        ClearLease();
        Version++;
    }

    public void Fail(long fencingToken, string? error, DateTimeOffset now)
    {
        EnsureFencing(fencingToken);
        RunStateMachine.EnsureTransition(State, RunState.Failed);
        State = RunState.Failed;
        Error = error;
        FinishedAt = now;
        Version++;
    }

    public void TimeOut(long fencingToken, DateTimeOffset now)
    {
        EnsureFencing(fencingToken);
        RunStateMachine.EnsureTransition(State, RunState.TimedOut);
        State = RunState.TimedOut;
        Error = "Run exceeded its timeout.";
        FinishedAt = now;
        Version++;
    }

    /// <summary>Cancels the run (cooperative). Allowed from any non-terminal state.</summary>
    public void Cancel(DateTimeOffset now)
    {
        RunStateMachine.EnsureTransition(State, RunState.Cancelled);
        State = RunState.Cancelled;
        Error ??= "Cancelled by request.";
        FinishedAt = now;
        ClearLease();
        Version++;
    }

    /// <summary>Flags an active run for cooperative cancellation without changing its state.</summary>
    public void RequestCancel() => CancelRequested = true;

    /// <summary>Moves a failed/timed-out run into Retrying then re-arms it as Pending at the delay.</summary>
    public void Retry(DateTimeOffset nextScheduledAt)
    {
        RunStateMachine.EnsureTransition(State, RunState.Retrying);
        State = RunState.Retrying;
        RunStateMachine.EnsureTransition(State, RunState.Pending);
        State = RunState.Pending;
        ScheduledAt = nextScheduledAt;
        FinishedAt = null;
        Error = null;
        Output = null;
        ClearLease();
        Version++;
    }

    public void DeadLetter()
    {
        RunStateMachine.EnsureTransition(State, RunState.DeadLettered);
        State = RunState.DeadLettered;
        ClearLease();
        Version++;
    }

    /// <summary>Reclaims an expired-lease run back to Pending for another node (reaper path).</summary>
    public void ReclaimExpired(DateTimeOffset now)
    {
        if (!RunStateMachine.IsActive(State))
        {
            throw new InvalidOperationException($"Only active runs can be reclaimed (state was {State}).");
        }
        // Claimed -> Pending is legal; Running is reset to Pending via the same reclaim intent.
        State = RunState.Pending;
        ClearLease();
        Version++;
    }

    /// <summary>Re-arms a dead-lettered run as a fresh Pending run (DLQ replay).</summary>
    public void ReplayReset(DateTimeOffset scheduledAt)
    {
        RunStateMachine.EnsureTransition(State, RunState.Pending);
        State = RunState.Pending;
        AttemptCount = 0;
        ScheduledAt = scheduledAt;
        Output = null;
        Error = null;
        FinishedAt = null;
        StartedAt = null;
        ClearLease();
        Version++;
    }

    public bool HasAttemptsRemaining() => AttemptCount < MaxAttempts;

    private void EnsureFencing(long presented)
    {
        if (presented != FencingToken)
        {
            throw new FencingTokenException(Id, presented, FencingToken);
        }
    }

    private void ClearLease()
    {
        LeaseOwner = null;
        LeaseToken = null;
        LeaseExpiresAt = null;
    }
}
