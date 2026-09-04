using ReconEngine.Domain.Enums;
using ReconEngine.Domain.ValueObjects;

namespace ReconEngine.Api.Contracts;

// ---- requests ----

public sealed record TokenRequest(string? Subject, string[]? Scopes);

public sealed record CreateRuleSetRequest(string Name, string? Description, bool Activate, MatchingRuleSetDefinition? Definition);

public sealed record StartRunRequest(Guid? RuleSetId, DateOnly? From, DateOnly? To);

public sealed record AssignRequest(string Assignee);

public sealed record CommentRequest(string Text);

public sealed record ResolveRequest(ResolutionReasonCode Reason, string? Note);

public sealed record NoteRequest(string? Note);

// ---- responses ----

public sealed record TokenResponse(string AccessToken, DateTime ExpiresAtUtc, string TokenType = "Bearer");

public sealed record RuleSetResponse(Guid Id, string Name, int Version, string VersionTag, bool IsActive, string Description, DateTime CreatedAtUtc);

public sealed record RunResponse(
    Guid Id,
    string RuleSetVersionTag,
    RunStatus Status,
    DateOnly? WindowFrom,
    DateOnly? WindowTo,
    string InputChecksum,
    int InternalRecordCount,
    int ExternalRecordCount,
    int MatchCount,
    int MatchedInternalCount,
    int MatchedExternalCount,
    int CarriedForwardCount,
    int ExceptionCount,
    bool BalanceAssertionPassed,
    string? BalanceAssertionDetail,
    long DurationMs,
    DateTime StartedAtUtc,
    DateTime? CompletedAtUtc);

public sealed record ExceptionCommentResponse(string Author, string Text, DateTime CreatedAtUtc);

public sealed record ExceptionAuditResponse(string Actor, string Action, string FromStatus, string ToStatus, string? Detail, DateTime AtUtc);

public sealed record ExceptionResponse(
    Guid Id,
    string ExceptionKey,
    ExceptionType Type,
    ExceptionSeverity Severity,
    ExceptionStatus Status,
    string Currency,
    long AmountMinor,
    string SuggestedAction,
    string? AssignedTo,
    bool ApprovalRequired,
    string? ResolvedBy,
    ResolutionReasonCode? ResolutionReasonCode,
    string? ApprovedBy,
    IReadOnlyList<Guid> RecordIds,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc,
    IReadOnlyList<ExceptionCommentResponse> Comments,
    IReadOnlyList<ExceptionAuditResponse> AuditTrail);
