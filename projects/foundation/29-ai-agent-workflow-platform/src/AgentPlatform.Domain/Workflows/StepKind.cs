namespace AgentPlatform.Domain.Workflows;

/// <summary>Discriminates the kind of a workflow step.</summary>
public enum StepKind
{
    Model,
    Tool,
    Condition,
    Parallel,
    Loop,
    HumanApproval,
    Transform,
    Terminal,
}

/// <summary>How a run finished, as declared by the terminal step it reached.</summary>
public enum WorkflowOutcome
{
    Succeeded,
    Escalated,
    Rejected,
    Failed,
}

/// <summary>Comparison operators available to a deterministic <see cref="ConditionStep"/>.</summary>
public enum ConditionOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Contains,
    Exists,
}

/// <summary>Default action taken when a human approval step times out.</summary>
public enum ApprovalDefault
{
    Reject,
    Approve,
    Escalate,
}
