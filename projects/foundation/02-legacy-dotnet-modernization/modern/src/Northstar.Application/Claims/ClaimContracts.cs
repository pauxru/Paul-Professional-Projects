using Northstar.Domain.Claims;

namespace Northstar.Application.Claims;

public sealed record CreatePolicyholderCommand(string Name, string Email);

public sealed record CreatePolicyCommand(
    Guid PolicyholderId,
    string PolicyNumber,
    decimal DeductibleAmount,
    decimal LimitAmount,
    string Currency);

public sealed record IntakeClaimCommand(
    string PolicyNumber,
    string Reference,
    decimal ClaimedAmount,
    string Currency);

public sealed record AssessClaimCommand(
    string Adjuster,
    decimal ReserveAmount,
    int ExpectedVersion);

public sealed record TransitionClaimCommand(ClaimStatus TargetStatus, int ExpectedVersion);

public sealed record AttachDocumentCommand(
    string OriginalName,
    string ContentType,
    byte[] Content,
    int ExpectedVersion);

public sealed record ClaimResponse(
    Guid Id,
    string Reference,
    string PolicyNumber,
    string? Policyholder,
    ClaimStatus Status,
    decimal ClaimedAmount,
    decimal ReserveAmount,
    decimal SettlementAmount,
    string Currency,
    string? AssignedAdjuster,
    int Version,
    DateTimeOffset CreatedAt);

public sealed record ClaimListResponse(
    IReadOnlyList<ClaimResponse> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed class ConcurrencyConflictException(string message) : Exception(message);

public sealed class ResourceNotFoundException(string message) : Exception(message);
