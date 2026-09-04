namespace AgentPlatform.Domain.Tracing;

/// <summary>Kinds of events recorded in a run's execution trace.</summary>
public enum TraceEventType
{
    RunStarted,
    StepStarted,
    ModelCall,
    ToolCall,
    Decision,
    StateTransition,
    Retry,
    ApprovalRequested,
    ApprovalDecided,
    BudgetHalt,
    StepCompleted,
    StepFailed,
    RunCompleted,
}

/// <summary>
/// One immutable entry in a run's replayable execution trace. Together, ordered by
/// <see cref="Ordinal"/>, these entries fully describe what happened: each model call (with prompt
/// version, tokens and latency), each tool call (arguments, result/error, cost, duration), each
/// deterministic decision and each state transition.
/// </summary>
public sealed class TraceEvent
{
    private TraceEvent() { } // EF

    public TraceEvent(string id, string runId, int ordinal, TraceEventType type, string? stepId,
        DateTimeOffset timestamp, string dataJson, long durationMs = 0,
        int promptTokens = 0, int completionTokens = 0, decimal cost = 0m,
        string? promptVersion = null, string? toolName = null, bool success = true)
    {
        Id = id;
        RunId = runId;
        Ordinal = ordinal;
        Type = type;
        StepId = stepId;
        Timestamp = timestamp;
        DataJson = dataJson;
        DurationMs = durationMs;
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        Cost = cost;
        PromptVersion = promptVersion;
        ToolName = toolName;
        Success = success;
    }

    public string Id { get; private set; } = string.Empty;
    public string RunId { get; private set; } = string.Empty;
    public int Ordinal { get; private set; }
    public TraceEventType Type { get; private set; }
    public string? StepId { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public string DataJson { get; private set; } = "{}";
    public long DurationMs { get; private set; }
    public int PromptTokens { get; private set; }
    public int CompletionTokens { get; private set; }
    public decimal Cost { get; private set; }
    public string? PromptVersion { get; private set; }
    public string? ToolName { get; private set; }
    public bool Success { get; private set; }
}
