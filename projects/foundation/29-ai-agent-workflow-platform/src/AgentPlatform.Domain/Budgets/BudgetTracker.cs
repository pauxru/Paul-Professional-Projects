using AgentPlatform.Domain.Common;

namespace AgentPlatform.Domain.Budgets;

/// <summary>
/// Mutable running total of resource consumption for a run, checked against
/// <see cref="BudgetLimits"/> after each model/tool call. This is a pure domain object; the
/// engine persists a snapshot of these counters with the run so budgets survive resume.
/// </summary>
public sealed class BudgetTracker
{
    private readonly BudgetLimits _limits;
    private readonly string _currency;

    public BudgetTracker(BudgetLimits limits, DateTimeOffset startedAt, string currency = "USD",
        int tokensUsed = 0, decimal costUsed = 0m, int toolCalls = 0, int modelCalls = 0, long outputBytes = 0)
    {
        _limits = limits;
        _currency = currency;
        StartedAt = startedAt;
        TokensUsed = tokensUsed;
        CostUsed = costUsed;
        ToolCalls = toolCalls;
        ModelCalls = modelCalls;
        OutputBytes = outputBytes;
    }

    public DateTimeOffset StartedAt { get; }
    public int TokensUsed { get; private set; }
    public decimal CostUsed { get; private set; }
    public int ToolCalls { get; private set; }
    public int ModelCalls { get; private set; }
    public long OutputBytes { get; private set; }

    public Money Cost => new(CostUsed, _currency);

    public void AddModelUsage(int tokens, decimal cost)
    {
        TokensUsed += tokens;
        CostUsed += cost;
        ModelCalls++;
    }

    public void AddToolUsage(decimal cost, int outputBytes)
    {
        CostUsed += cost;
        ToolCalls++;
        OutputBytes += outputBytes;
    }

    /// <summary>Returns the first breached dimension, or <see cref="BudgetHaltReason.None"/>.</summary>
    public BudgetHaltReason Evaluate(DateTimeOffset now)
    {
        if (_limits.MaxTokens > 0 && TokensUsed > _limits.MaxTokens) return BudgetHaltReason.TokenBudgetExceeded;
        if (_limits.MaxCost > 0 && CostUsed > _limits.MaxCost) return BudgetHaltReason.CostBudgetExceeded;
        if (_limits.MaxToolCalls > 0 && ToolCalls > _limits.MaxToolCalls) return BudgetHaltReason.ToolCallBudgetExceeded;
        if (_limits.MaxModelCalls > 0 && ModelCalls > _limits.MaxModelCalls) return BudgetHaltReason.ModelCallBudgetExceeded;
        if (_limits.MaxOutputBytes > 0 && OutputBytes > _limits.MaxOutputBytes) return BudgetHaltReason.OutputSizeExceeded;
        if (_limits.MaxWallClockSeconds > 0 && (now - StartedAt).TotalSeconds > _limits.MaxWallClockSeconds)
            return BudgetHaltReason.WallClockExceeded;
        return BudgetHaltReason.None;
    }

    /// <summary>Would a single output of this size breach the per-call output cap?</summary>
    public bool ExceedsOutputCap(int bytes) => _limits.MaxOutputBytes > 0 && bytes > _limits.MaxOutputBytes;
}
