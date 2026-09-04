namespace Idp.Domain.Review;

/// <summary>Thrown when two reviewers race to claim the same task.</summary>
public sealed class ReviewClaimConflictException : Exception
{
    public ReviewClaimConflictException(Guid taskId, string heldBy)
        : base($"Review task {taskId} is already claimed by '{heldBy}'.") { }
}

/// <summary>
/// A human-review work item for a document routed to InReview. Supports priority ordering,
/// claim/lock with an expiry lease (so an abandoned claim frees up), SLA aging and completion.
/// </summary>
public sealed class ReviewTask
{
    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public ReviewStatus Status { get; private set; }
    public ReviewResolution Resolution { get; private set; }

    public int Priority { get; private set; }
    public decimal? DocumentValue { get; private set; }
    public double DocumentConfidence { get; private set; }

    public string? ClaimedBy { get; private set; }
    public DateTime? ClaimedAtUtc { get; private set; }
    public DateTime? ClaimExpiresUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }
    public DateTime SlaDueUtc { get; private set; }
    public DateTime? CompletedAtUtc { get; private set; }
    public string? CompletedBy { get; private set; }

    private ReviewTask() { }

    public ReviewTask(
        Guid documentId, int priority, decimal? documentValue, double documentConfidence,
        DateTime nowUtc, TimeSpan slaWindow)
    {
        Id = Guid.NewGuid();
        DocumentId = documentId;
        Status = ReviewStatus.Pending;
        Resolution = ReviewResolution.None;
        Priority = priority;
        DocumentValue = documentValue;
        DocumentConfidence = documentConfidence;
        CreatedAtUtc = nowUtc;
        SlaDueUtc = nowUtc + slaWindow;
    }

    public bool IsClaimActive(DateTime nowUtc) =>
        Status == ReviewStatus.Claimed && ClaimExpiresUtc is { } exp && exp > nowUtc;

    public bool IsClaimable(DateTime nowUtc) =>
        Status == ReviewStatus.Pending ||
        (Status == ReviewStatus.Claimed && !IsClaimActive(nowUtc));

    public bool IsOverdue(DateTime nowUtc) =>
        Status != ReviewStatus.Completed && nowUtc > SlaDueUtc;

    public TimeSpan Age(DateTime nowUtc) => nowUtc - CreatedAtUtc;

    /// <summary>Claim the task for a reviewer for a bounded lease. Expired claims can be taken over.</summary>
    public void Claim(string reviewer, DateTime nowUtc, TimeSpan lease)
    {
        if (Status == ReviewStatus.Completed)
            throw new InvalidOperationException($"Review task {Id} is already completed.");
        if (IsClaimActive(nowUtc) &&
            !string.Equals(ClaimedBy, reviewer, StringComparison.OrdinalIgnoreCase))
            throw new ReviewClaimConflictException(Id, ClaimedBy!);

        Status = ReviewStatus.Claimed;
        ClaimedBy = reviewer;
        ClaimedAtUtc = nowUtc;
        ClaimExpiresUtc = nowUtc + lease;
    }

    public void Release()
    {
        if (Status != ReviewStatus.Completed)
        {
            Status = ReviewStatus.Pending;
            ClaimedBy = null;
            ClaimedAtUtc = null;
            ClaimExpiresUtc = null;
        }
    }

    public void Complete(ReviewResolution resolution, string reviewer, DateTime nowUtc)
    {
        if (Status == ReviewStatus.Completed) return;
        Status = ReviewStatus.Completed;
        Resolution = resolution;
        CompletedBy = reviewer;
        CompletedAtUtc = nowUtc;
    }
}
