using Lakehouse.Application.Pipelines;

namespace Lakehouse.Application.Orchestration;

/// <summary>The window a run targets — a business date range label or "full" for the whole feed.</summary>
public sealed record RunContext(string RunId, string Window)
{
    public static RunContext Full(string runId) => new(runId, "full");
}

/// <summary>Outcome of a single task in a run.</summary>
public enum TaskState { Succeeded, Failed, Skipped, Blocked }

/// <summary>A task in the pipeline DAG: an id, its upstream dependencies, a retry budget and the work.</summary>
public sealed class PipelineTask
{
    public required string Id { get; init; }
    public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
    public int MaxRetries { get; init; }

    /// <summary>The unit of work. Returns row-count telemetry; may throw to signal failure.</summary>
    public required Func<RunContext, StepResult> Run { get; init; }
}

/// <summary>Per-task result captured in run history (timings, row counts, attempts, status).</summary>
public sealed record TaskResult(
    string TaskId,
    TaskState State,
    long RowsIn,
    long RowsOut,
    long Quarantined,
    int Attempts,
    double DurationMs,
    string? Error = null,
    string? Note = null);

/// <summary>The full record of one DAG run, persisted to run history.</summary>
public sealed record RunRecord(
    string RunId,
    string Window,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    IReadOnlyList<TaskResult> Tasks)
{
    public bool Success => Tasks.All(t => t.State is TaskState.Succeeded or TaskState.Skipped);
    public double DurationMs => (FinishedAt - StartedAt).TotalMilliseconds;
    public int Failed => Tasks.Count(t => t.State == TaskState.Failed);
    public int Blocked => Tasks.Count(t => t.State == TaskState.Blocked);
    public long TotalRowsOut => Tasks.Sum(t => t.RowsOut);
}
