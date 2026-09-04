namespace AgentPlatform.Domain.Runs;

/// <summary>Lifecycle state of a workflow run — the run-level state machine.</summary>
public enum RunState
{
    Pending,
    Running,
    WaitingForApproval,
    Completed,
    Failed,
    Cancelled,
    Halted,
}

/// <summary>Status of a single step execution attempt.</summary>
public enum StepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped,
    WaitingForApproval,
}
