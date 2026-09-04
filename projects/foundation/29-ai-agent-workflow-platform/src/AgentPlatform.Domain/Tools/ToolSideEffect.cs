namespace AgentPlatform.Domain.Tools;

/// <summary>
/// Side-effect classification of a tool. Drives approval requirements, idempotency handling
/// and which failures may be retried.
/// </summary>
public enum ToolSideEffect
{
    /// <summary>No observable change; safe to retry freely (e.g. search, lookup).</summary>
    ReadOnly,

    /// <summary>Changes platform-owned state; must be at-most-once via an idempotency key.</summary>
    Mutating,

    /// <summary>Calls a third party (email, webhook); requires approval above a risk threshold.</summary>
    External,

    /// <summary>Expensive (tokens/compute); counts heavily against budgets.</summary>
    Costly,
}

/// <summary>Coarse risk rating used to decide whether a call needs human approval.</summary>
public enum ToolRiskLevel
{
    Low,
    Medium,
    High,
}
