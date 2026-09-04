using AgentPlatform.Domain.Workflows;

namespace AgentPlatform.Domain.Runs;

/// <summary>
/// A durable record of one attempt to execute one step of a run. Persisting these is what makes a
/// run resumable: on restart the engine skips steps that already have a <see cref="StepStatus.Completed"/>
/// execution and continues from the first that does not.
/// </summary>
public sealed class StepExecution
{
    // EF materialisation constructor.
    private StepExecution() { }

    public StepExecution(string id, string runId, string stepId, StepKind kind, int attempt, int ordinal)
    {
        Id = id;
        RunId = runId;
        StepId = stepId;
        Kind = kind;
        Attempt = attempt;
        Ordinal = ordinal;
        Status = StepStatus.Running;
    }

    public string Id { get; private set; } = string.Empty;
    public string RunId { get; private set; } = string.Empty;
    public string StepId { get; private set; } = string.Empty;
    public StepKind Kind { get; private set; }
    public int Attempt { get; private set; }

    /// <summary>Monotonic execution order within the run, for deterministic trace/replay ordering.</summary>
    public int Ordinal { get; private set; }

    public StepStatus Status { get; private set; }
    public string? InputJson { get; private set; }
    public string? OutputJson { get; private set; }
    public string? Error { get; private set; }
    public string? ErrorCode { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public long DurationMs { get; private set; }

    public void Begin(DateTimeOffset now, string? inputJson)
    {
        StartedAt = now;
        InputJson = inputJson;
        Status = StepStatus.Running;
    }

    public void Complete(DateTimeOffset now, string? outputJson)
    {
        Status = StepStatus.Completed;
        OutputJson = outputJson;
        CompletedAt = now;
        DurationMs = (long)(now - StartedAt).TotalMilliseconds;
    }

    public void Fail(DateTimeOffset now, string error, string? code)
    {
        Status = StepStatus.Failed;
        Error = error;
        ErrorCode = code;
        CompletedAt = now;
        DurationMs = (long)(now - StartedAt).TotalMilliseconds;
    }

    public void MarkWaitingForApproval(DateTimeOffset now)
    {
        Status = StepStatus.WaitingForApproval;
        CompletedAt = now;
        DurationMs = (long)(now - StartedAt).TotalMilliseconds;
    }
}
