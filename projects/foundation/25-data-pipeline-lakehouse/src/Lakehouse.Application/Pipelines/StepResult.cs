namespace Lakehouse.Application.Pipelines;

/// <summary>Row-count telemetry for one pipeline step, surfaced in run history and observability.</summary>
public sealed record StepResult(string Step, long RowsIn, long RowsOut, long Quarantined = 0, string? Note = null);
