using Healthcare.Domain.Common;

namespace Healthcare.Domain.Waitlist;

public enum WaitlistPriority
{
    Standard = 0,
    Elevated = 1,
    Urgent = 2
}

public enum WaitlistStatus
{
    Active = 0,
    Offered = 1,
    Accepted = 2,
    Expired = 3,
    Cancelled = 4
}

public sealed class WaitlistEntry : Entity
{
    public Guid PatientId { get; private set; }
    public Guid FacilityId { get; private set; }
    public Guid AppointmentTypeId { get; private set; }
    public WaitlistPriority Priority { get; private set; }
    public WaitlistStatus Status { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? OfferedAtUtc { get; private set; }
    public DateTimeOffset? OfferExpiresAtUtc { get; private set; }
    public Guid? OfferedAppointmentId { get; private set; }

    private WaitlistEntry() { }

    public static WaitlistEntry Create(Guid patientId, Guid facilityId, Guid appointmentTypeId,
        WaitlistPriority priority, DateTimeOffset now) => new()
        {
            PatientId = patientId,
            FacilityId = facilityId,
            AppointmentTypeId = appointmentTypeId,
            Priority = priority,
            Status = WaitlistStatus.Active,
            CreatedAtUtc = now
        };

    public void OfferSlot(Guid appointmentId, TimeSpan acceptanceWindow, DateTimeOffset now)
    {
        if (Status != WaitlistStatus.Active)
            throw new DomainException("waitlist.state.invalid", $"Cannot offer slot from status {Status}.");
        Status = WaitlistStatus.Offered;
        OfferedAppointmentId = appointmentId;
        OfferedAtUtc = now;
        OfferExpiresAtUtc = now.Add(acceptanceWindow);
    }

    public void Accept(DateTimeOffset now)
    {
        if (Status != WaitlistStatus.Offered)
            throw new DomainException("waitlist.state.invalid", $"Cannot accept from status {Status}.");
        if (OfferExpiresAtUtc is null || now > OfferExpiresAtUtc)
            throw new DomainException("waitlist.offer.expired", "Offer has expired.");
        Status = WaitlistStatus.Accepted;
    }

    public void Expire(DateTimeOffset now)
    {
        if (Status != WaitlistStatus.Offered) return;
        if (OfferExpiresAtUtc is not null && now > OfferExpiresAtUtc)
        {
            Status = WaitlistStatus.Expired;
        }
    }

    public void Cancel()
    {
        if (Status is WaitlistStatus.Accepted) return;
        Status = WaitlistStatus.Cancelled;
    }
}
