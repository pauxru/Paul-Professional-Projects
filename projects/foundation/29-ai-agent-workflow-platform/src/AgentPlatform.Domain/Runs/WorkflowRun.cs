using AgentPlatform.Domain.Budgets;
using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Domain.Runs;

/// <summary>
/// The central durable aggregate: one execution of a workflow. Its <see cref="StateJson"/> bag,
/// <see cref="CurrentStepId"/> and budget counters are persisted after every step so the run can
/// be resumed exactly where it stopped after a crash or an approval pause.
/// </summary>
public sealed class WorkflowRun
{
    private readonly List<StepExecution> _stepExecutions = new();

    private WorkflowRun() { } // EF

    public WorkflowRun(string id, string workflowName, int workflowVersion, string tenantId,
        string createdBy, string startStepId, string stateJson, string budgetJson,
        string correlationId, DateTimeOffset now, string? idempotencyKey, string grantedScopes)
    {
        Id = id;
        WorkflowName = workflowName;
        WorkflowVersion = workflowVersion;
        TenantId = tenantId;
        CreatedBy = createdBy;
        CurrentStepId = startStepId;
        StateJson = stateJson;
        BudgetJson = budgetJson;
        CorrelationId = correlationId;
        IdempotencyKey = idempotencyKey;
        GrantedScopes = grantedScopes;
        Status = RunState.Pending;
        CreatedAt = now;
        UpdatedAt = now;
    }

    public string Id { get; private set; } = string.Empty;
    public string WorkflowName { get; private set; } = string.Empty;
    public int WorkflowVersion { get; private set; }
    public string TenantId { get; private set; } = string.Empty;
    public string CreatedBy { get; private set; } = string.Empty;
    public string? IdempotencyKey { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string GrantedScopes { get; private set; } = string.Empty;

    public RunState Status { get; private set; }
    public string? CurrentStepId { get; private set; }
    public string StateJson { get; private set; } = "{}";
    public string BudgetJson { get; private set; } = "{}";

    public WorkflowOutcome? Outcome { get; private set; }
    public BudgetHaltReason HaltReason { get; private set; } = BudgetHaltReason.None;
    public string? Error { get; private set; }
    public string? ResultMessage { get; private set; }

    // Budget counters (snapshot, restored into a BudgetTracker on resume).
    public int TokensUsed { get; private set; }
    public decimal CostUsed { get; private set; }
    public int ToolCalls { get; private set; }
    public int ModelCalls { get; private set; }
    public long OutputBytes { get; private set; }
    public int NextOrdinal { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Optimistic concurrency token.</summary>
    public int Version { get; private set; }

    public IReadOnlyList<StepExecution> StepExecutions => _stepExecutions;

    public bool IsTerminal => Status is RunState.Completed or RunState.Failed or RunState.Cancelled or RunState.Halted;

    public void MarkRunning(DateTimeOffset now)
    {
        if (Status == RunState.Pending) StartedAt = now;
        Status = RunState.Running;
        Touch(now);
    }

    public int TakeOrdinal() => NextOrdinal++;

    public void AddStepExecution(StepExecution execution) => _stepExecutions.Add(execution);

    public void AdvanceTo(string? nextStepId, string stateJson, DateTimeOffset now)
    {
        CurrentStepId = nextStepId;
        StateJson = stateJson;
        Touch(now);
    }

    public void UpdateBudget(int tokensUsed, decimal costUsed, int toolCalls, int modelCalls,
        long outputBytes, string budgetJson, DateTimeOffset now)
    {
        TokensUsed = tokensUsed;
        CostUsed = costUsed;
        ToolCalls = toolCalls;
        ModelCalls = modelCalls;
        OutputBytes = outputBytes;
        BudgetJson = budgetJson;
        Touch(now);
    }

    public void PauseForApproval(DateTimeOffset now)
    {
        Status = RunState.WaitingForApproval;
        Touch(now);
    }

    public void ResumeFromApproval(string nextStepId, string stateJson, DateTimeOffset now)
    {
        Status = RunState.Running;
        CurrentStepId = nextStepId;
        StateJson = stateJson;
        Touch(now);
    }

    public void Complete(WorkflowOutcome outcome, string? message, DateTimeOffset now)
    {
        Status = outcome == WorkflowOutcome.Failed ? RunState.Failed : RunState.Completed;
        Outcome = outcome;
        ResultMessage = message;
        CurrentStepId = null;
        CompletedAt = now;
        Touch(now);
    }

    public void Halt(BudgetHaltReason reason, string message, DateTimeOffset now)
    {
        Status = RunState.Halted;
        HaltReason = reason;
        ResultMessage = message;
        Outcome = WorkflowOutcome.Escalated;
        CompletedAt = now;
        Touch(now);
    }

    public void Fail(string error, DateTimeOffset now)
    {
        Status = RunState.Failed;
        Outcome = WorkflowOutcome.Failed;
        Error = error;
        CompletedAt = now;
        Touch(now);
    }

    public bool Cancel(DateTimeOffset now)
    {
        if (IsTerminal) return false;
        Status = RunState.Cancelled;
        Outcome = WorkflowOutcome.Failed;
        CompletedAt = now;
        Touch(now);
        return true;
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Version++;
    }
}
