using ReconEngine.Domain.Enums;

namespace ReconEngine.Application.Common;

/// <summary>Filter/sort parameters for querying reconciliation exceptions.</summary>
public sealed record ExceptionQuery(
    ExceptionStatus? Status = null,
    ExceptionType? Type = null,
    ExceptionSeverity? Severity = null,
    string? Currency = null,
    string? AssignedTo = null,
    string SortBy = "createdAt",
    bool Descending = true,
    int Skip = 0,
    int Take = 50);

/// <summary>Filter parameters for querying normalised records.</summary>
public sealed record RecordQuery(
    RecordSource? Source = null,
    string? Currency = null,
    ReconStatus? Status = null,
    int Skip = 0,
    int Take = 50);
