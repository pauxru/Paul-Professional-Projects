using Idp.Domain.Documents;
using Idp.Domain.Review;

namespace Idp.Application.Review;

/// <summary>A prioritised review-queue row projected for the API and review UI.</summary>
public sealed record ReviewQueueItemDto(
    Guid TaskId,
    Guid DocumentId,
    string FileName,
    DocumentType DocumentType,
    int Priority,
    decimal? DocumentValue,
    double DocumentConfidence,
    ReviewStatus Status,
    string? ClaimedBy,
    DateTime? ClaimExpiresUtc,
    DateTime SlaDueUtc,
    bool Overdue,
    int AgeMinutes);

/// <summary>A single field edit captured during review.</summary>
public sealed record FieldCorrectionInput(string FieldKey, string? NewValue, string Reason);

public enum ReviewActionStatus { Ok, NotFound, Conflict, Invalid }

public sealed record ReviewActionResult(ReviewActionStatus Status, string? Message = null)
{
    public bool Success => Status == ReviewActionStatus.Ok;
    public static ReviewActionResult Ok(string? message = null) => new(ReviewActionStatus.Ok, message);
    public static ReviewActionResult NotFound(string message) =>
        new(ReviewActionStatus.NotFound, message);
    public static ReviewActionResult Conflict(string message) =>
        new(ReviewActionStatus.Conflict, message);
    public static ReviewActionResult Invalid(string message) =>
        new(ReviewActionStatus.Invalid, message);
}
