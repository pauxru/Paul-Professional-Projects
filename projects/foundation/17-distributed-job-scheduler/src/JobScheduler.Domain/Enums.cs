namespace JobScheduler.Domain;

/// <summary>
/// Lifecycle states for a single job run (instance). The legal transitions are
/// enforced by <see cref="RunStateMachine"/>; persistence never sets state directly.
/// </summary>
public enum RunState
{
    Pending = 0,
    Claimed = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Retrying = 5,
    TimedOut = 6,
    Cancelled = 7,
    DeadLettered = 8
}

/// <summary>How a job definition is scheduled.</summary>
public enum TriggerType
{
    Manual = 0,
    OneOff = 1,
    Cron = 2,
    Interval = 3
}

/// <summary>Backoff family used to compute the delay before the next attempt.</summary>
public enum RetryStrategy
{
    Fixed = 0,
    Exponential = 1,
    ExponentialJitter = 2
}

/// <summary>
/// What to do when a scheduled fire time was missed (e.g. the scheduler was down
/// or the leader was mid-failover) and is only being evaluated after the fact.
/// </summary>
public enum MisfirePolicy
{
    /// <summary>Fire exactly one run now for the whole missed window.</summary>
    FireNow = 0,

    /// <summary>Ignore everything that was missed and wait for the next scheduled time.</summary>
    SkipToNext = 1,

    /// <summary>Enqueue every missed occurrence (bounded by a cap) to catch up.</summary>
    RunAllMissed = 2
}

/// <summary>Health/availability of a worker node in the registry.</summary>
public enum WorkerStatus
{
    Active = 0,
    Draining = 1,
    Dead = 2
}
