namespace AgentPlatform.Domain.Budgets;

/// <summary>
/// Caps applied to a run (and, aggregated, to a tenant). A zero/negative value means "no limit"
/// for that dimension. Budgets are hard guardrails: exceeding any dimension halts the run.
/// </summary>
public sealed record BudgetLimits
{
    public int MaxTokens { get; init; }
    public decimal MaxCost { get; init; }
    public int MaxToolCalls { get; init; }
    public int MaxModelCalls { get; init; }
    public int MaxWallClockSeconds { get; init; }
    public int MaxOutputBytes { get; init; }

    public static BudgetLimits Default { get; } = new()
    {
        MaxTokens = 40_000,
        MaxCost = 5.00m,
        MaxToolCalls = 40,
        MaxModelCalls = 25,
        MaxWallClockSeconds = 120,
        MaxOutputBytes = 64 * 1024,
    };

    public static BudgetLimits Unlimited { get; } = new();
}

/// <summary>Reasons a run can be halted by a guardrail.</summary>
public enum BudgetHaltReason
{
    None,
    TokenBudgetExceeded,
    CostBudgetExceeded,
    ToolCallBudgetExceeded,
    ModelCallBudgetExceeded,
    WallClockExceeded,
    OutputSizeExceeded,
    LoopDetected,
}
