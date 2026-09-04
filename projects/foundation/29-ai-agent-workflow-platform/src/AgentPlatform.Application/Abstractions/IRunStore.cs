using AgentPlatform.Domain.Approvals;
using AgentPlatform.Domain.Prompts;
using AgentPlatform.Domain.Runs;
using AgentPlatform.Domain.Tracing;
using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Application.Abstractions;

/// <summary>Unit of work over the shared persistence context; commits all staged changes.</summary>
public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}

/// <summary>Persistence for workflow runs and their child step executions and trace events.</summary>
public interface IRunStore
{
    void Add(WorkflowRun run);

    /// <summary>Load a run with its step executions (for resume/skip decisions). Null if absent.</summary>
    Task<WorkflowRun?> GetAsync(string runId, CancellationToken cancellationToken);

    Task<WorkflowRun?> GetByIdempotencyKeyAsync(string tenantId, string idempotencyKey, CancellationToken cancellationToken);

    void AddStepExecution(StepExecution execution);

    void AddTraceEvent(TraceEvent traceEvent);

    Task<IReadOnlyList<TraceEvent>> GetTraceAsync(string runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowRun>> ListAsync(string? tenantId, int page, int pageSize, CancellationToken cancellationToken);

    Task<int> CountAsync(string? tenantId, CancellationToken cancellationToken);
}

/// <summary>Persistence for approval tasks.</summary>
public interface IApprovalStore
{
    void Add(ApprovalTask approval);

    Task<ApprovalTask?> GetAsync(string approvalId, CancellationToken cancellationToken);

    Task<ApprovalTask?> GetPendingForRunAsync(string runId, CancellationToken cancellationToken);

    /// <summary>
    /// The most recent approval raised for a run regardless of status. The engine uses this when
    /// resuming, because by then the decision has already moved the task out of the pending state.
    /// </summary>
    Task<ApprovalTask?> GetLatestForRunAsync(string runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ApprovalTask>> ListPendingAsync(string? tenantId, CancellationToken cancellationToken);
}

/// <summary>
/// Persistence for idempotency records that guarantee at-most-once execution of mutating tools.
/// A record stores the serialized result so a retry returns the original outcome.
/// </summary>
public interface IIdempotencyStore
{
    Task<string?> TryGetResultAsync(string key, CancellationToken cancellationToken);

    /// <summary>Stage a record; committed atomically with the tool's side effect by the caller.</summary>
    void Add(string key, string runId, string toolName, string resultJson, DateTimeOffset now);
}

/// <summary>Read-only registry of validated workflow definitions (all versions).</summary>
public interface IWorkflowRegistry
{
    WorkflowDefinition? Get(string name, int version);

    WorkflowDefinition? GetLatest(string name);

    IReadOnlyCollection<WorkflowDefinition> All { get; }

    IReadOnlyCollection<int> Versions(string name);
}

/// <summary>Read-only registry of versioned prompt templates.</summary>
public interface IPromptRegistry
{
    PromptTemplate? Get(string name, int version);

    PromptTemplate? GetLatest(string name);

    IReadOnlyCollection<PromptTemplate> All { get; }
}
