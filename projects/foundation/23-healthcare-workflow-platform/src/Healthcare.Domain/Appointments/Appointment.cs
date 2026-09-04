using Healthcare.Domain.Common;

namespace Healthcare.Domain.Appointments;

public enum CancellationReason
{
    PatientRequest = 0,
    ClinicianUnavailable = 1,
    ClinicClosure = 2,
    NoShowPolicy = 3,
    SystemError = 4,
    Other = 99
}

public sealed class Appointment : Entity
{
    public Guid PatientId { get; private set; }
    public Guid ClinicianId { get; private set; }
    public Guid FacilityId { get; private set; }
    public Guid RoomId { get; private set; }
    public Guid AppointmentTypeId { get; private set; }
    public DateTimeOffset StartUtc { get; private set; }
    public DateTimeOffset EndUtc { get; private set; }
    public AppointmentStatus Status { get; private set; } = AppointmentStatus.Booked;
    public CancellationReason? CancellationReason { get; private set; }
    public string? CancellationNotes { get; private set; }
    /// <summary>Optimistic concurrency token — SQLite rowversion analog.</summary>
    public byte[] RowVersion { get; private set; } = new byte[8];
    /// <summary>Optional recurring series id.</summary>
    public Guid? SeriesId { get; private set; }
    public bool NoShow { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? CheckedInAt { get; private set; }
    public DateTimeOffset? TriageStartedAt { get; private set; }
    public DateTimeOffset? WithClinicianAt { get; private set; }
    public DateTimeOffset? AwaitingResultsAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    private Appointment() { }

    public static Appointment Book(Guid patientId, Guid clinicianId, Guid facilityId, Guid roomId,
        Guid appointmentTypeId, DateTimeOffset startUtc, DateTimeOffset endUtc)
    {
        if (endUtc <= startUtc) throw new DomainException("appointment.time.invalid", "End must be after start.");
        return new Appointment
        {
            PatientId = patientId,
            ClinicianId = clinicianId,
            FacilityId = facilityId,
            RoomId = roomId,
            AppointmentTypeId = appointmentTypeId,
            StartUtc = startUtc,
            EndUtc = endUtc,
            Status = AppointmentStatus.Booked,
            RowVersion = Guid.NewGuid().ToByteArray().Take(8).ToArray()
        };
    }

    public void SetSeries(Guid seriesId) => SeriesId = seriesId;

    public void Reschedule(DateTimeOffset newStartUtc, DateTimeOffset newEndUtc)
    {
        if (Status is AppointmentStatus.Completed or AppointmentStatus.Cancelled or AppointmentStatus.NoShow)
            throw new DomainException("appointment.reschedule.forbidden",
                $"Cannot reschedule appointment in status {Status}.");
        if (newEndUtc <= newStartUtc) throw new DomainException("appointment.time.invalid", "End must be after start.");
        StartUtc = newStartUtc;
        EndUtc = newEndUtc;
        Status = AppointmentStatus.Booked;
        ConfirmedAt = null;
        RowVersion = Guid.NewGuid().ToByteArray().Take(8).ToArray();
    }

    public void Cancel(CancellationReason reason, string? notes, DateTimeOffset at)
    {
        if (Status is AppointmentStatus.Completed or AppointmentStatus.Cancelled)
            throw new DomainException("appointment.cancel.forbidden",
                $"Cannot cancel appointment in status {Status}.");
        Status = AppointmentStatus.Cancelled;
        CancellationReason = reason;
        CancellationNotes = notes;
    }

    public void Confirm(DateTimeOffset at)
    {
        RequireStatus(AppointmentStatus.Booked);
        Status = AppointmentStatus.Confirmed;
        ConfirmedAt = at;
    }

    public void CheckIn(DateTimeOffset at)
    {
        // May check in from Booked or Confirmed.
        if (Status is not AppointmentStatus.Booked and not AppointmentStatus.Confirmed)
            throw new DomainException("appointment.state.invalid",
                $"Cannot check in from status {Status}.");
        Status = AppointmentStatus.CheckedIn;
        CheckedInAt = at;
    }

    public void StartTriage(DateTimeOffset at)
    {
        RequireStatus(AppointmentStatus.CheckedIn);
        Status = AppointmentStatus.InTriage;
        TriageStartedAt = at;
    }

    public void AdmitToClinician(DateTimeOffset at)
    {
        if (Status is not AppointmentStatus.InTriage and not AppointmentStatus.CheckedIn)
            throw new DomainException("appointment.state.invalid",
                $"Cannot admit to clinician from status {Status}.");
        Status = AppointmentStatus.WithClinician;
        WithClinicianAt = at;
    }

    public void MoveToAwaitingResults(DateTimeOffset at)
    {
        RequireStatus(AppointmentStatus.WithClinician);
        Status = AppointmentStatus.AwaitingResults;
        AwaitingResultsAt = at;
    }

    public void Complete(DateTimeOffset at)
    {
        if (Status is not AppointmentStatus.WithClinician and not AppointmentStatus.AwaitingResults)
            throw new DomainException("appointment.state.invalid",
                $"Cannot complete from status {Status}.");
        Status = AppointmentStatus.Completed;
        CompletedAt = at;
    }

    public void MarkNoShow(DateTimeOffset at)
    {
        if (Status is not AppointmentStatus.Booked and not AppointmentStatus.Confirmed)
            throw new DomainException("appointment.state.invalid",
                $"Cannot mark no-show from status {Status}.");
        Status = AppointmentStatus.NoShow;
        NoShow = true;
    }

    private void RequireStatus(AppointmentStatus expected)
    {
        if (Status != expected)
            throw new DomainException("appointment.state.invalid",
                $"Expected status {expected}, was {Status}.");
    }
}
