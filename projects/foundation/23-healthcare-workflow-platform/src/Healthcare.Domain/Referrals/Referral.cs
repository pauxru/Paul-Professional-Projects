using Healthcare.Domain.Common;

namespace Healthcare.Domain.Referrals;

public enum ReferralPriority
{
    Routine = 0,
    Urgent = 1,
    TwoWeek = 2
}

public enum ReferralStatus
{
    Draft = 0,
    Submitted = 1,
    Triaged = 2,
    Accepted = 3,
    Rejected = 4,
    Completed = 5,
    Cancelled = 6
}

public sealed class Referral : Entity
{
    public Guid PatientId { get; private set; }
    public Guid ReferringClinicianId { get; private set; }
    public Guid? DestinationFacilityId { get; private set; }
    public string ExternalDestination { get; private set; } = string.Empty;
    public string Speciality { get; private set; } = string.Empty;
    public ReferralPriority Priority { get; private set; }
    public ReferralStatus Status { get; private set; }
    public string ReasonForReferral { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? SubmittedAtUtc { get; private set; }
    public DateTimeOffset? TriagedAtUtc { get; private set; }
    public DateTimeOffset? AcceptedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? RejectionReason { get; private set; }
    /// <summary>Deadline by which the referral must move to Accepted (SLA clock).</summary>
    public DateTimeOffset SlaDueUtc { get; private set; }
    public bool SlaBreached { get; private set; }

    private Referral() { }

    public static Referral Create(Guid patientId, Guid referringClinicianId,
        Guid? destinationFacilityId, string externalDestination, string speciality,
        ReferralPriority priority, string reasonForReferral, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(reasonForReferral))
            throw new DomainException("referral.reason.required", "Referral reason required.");
        if (destinationFacilityId is null && string.IsNullOrWhiteSpace(externalDestination))
            throw new DomainException("referral.destination.required",
                "Referral needs an internal facility id or an external destination.");
        var sla = priority switch
        {
            ReferralPriority.Routine => now.AddDays(28),
            ReferralPriority.Urgent => now.AddDays(7),
            ReferralPriority.TwoWeek => now.AddDays(14),
            _ => now.AddDays(28)
        };
        return new Referral
        {
            PatientId = patientId,
            ReferringClinicianId = referringClinicianId,
            DestinationFacilityId = destinationFacilityId,
            ExternalDestination = externalDestination ?? string.Empty,
            Speciality = speciality.Trim(),
            Priority = priority,
            Status = ReferralStatus.Draft,
            ReasonForReferral = reasonForReferral.Trim(),
            CreatedAtUtc = now,
            SlaDueUtc = sla
        };
    }

    public void Submit(DateTimeOffset at)
    {
        if (Status != ReferralStatus.Draft)
            throw new DomainException("referral.state.invalid", $"Cannot submit from status {Status}.");
        Status = ReferralStatus.Submitted;
        SubmittedAtUtc = at;
    }

    public void Triage(DateTimeOffset at)
    {
        if (Status != ReferralStatus.Submitted)
            throw new DomainException("referral.state.invalid", $"Cannot triage from status {Status}.");
        Status = ReferralStatus.Triaged;
        TriagedAtUtc = at;
    }

    public void Accept(DateTimeOffset at)
    {
        if (Status is not ReferralStatus.Triaged and not ReferralStatus.Submitted)
            throw new DomainException("referral.state.invalid", $"Cannot accept from status {Status}.");
        Status = ReferralStatus.Accepted;
        AcceptedAtUtc = at;
    }

    public void Reject(string reason, DateTimeOffset at)
    {
        if (Status is not ReferralStatus.Triaged and not ReferralStatus.Submitted)
            throw new DomainException("referral.state.invalid", $"Cannot reject from status {Status}.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new DomainException("referral.reject.reason_required", "Rejection reason is required.");
        Status = ReferralStatus.Rejected;
        RejectionReason = reason;
    }

    public void Complete(DateTimeOffset at)
    {
        if (Status != ReferralStatus.Accepted)
            throw new DomainException("referral.state.invalid", $"Cannot complete from status {Status}.");
        Status = ReferralStatus.Completed;
        CompletedAtUtc = at;
    }

    /// <summary>Evaluates SLA at 'now' and marks breached if past the due time and not accepted.</summary>
    public bool EvaluateSla(DateTimeOffset now)
    {
        if (Status is ReferralStatus.Accepted or ReferralStatus.Completed or ReferralStatus.Rejected or ReferralStatus.Cancelled)
            return SlaBreached;
        if (now > SlaDueUtc)
        {
            SlaBreached = true;
        }
        return SlaBreached;
    }
}
