using Healthcare.Domain.Common;

namespace Healthcare.Domain.Reminders;

public enum ReminderChannel
{
    Sms = 0,
    Email = 1
}

public enum ReminderStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2,
    DeadLettered = 3,
    Cancelled = 4
}

public sealed class Reminder : Entity
{
    public Guid AppointmentId { get; private set; }
    public Guid PatientId { get; private set; }
    public ReminderChannel Channel { get; private set; }
    public int LeadTimeMinutes { get; private set; }
    public DateTimeOffset ScheduledSendAtUtc { get; private set; }
    public ReminderStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset? LastAttemptAtUtc { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Composite idempotency string appointmentId|leadMinutes to prevent duplicate scheduling.</summary>
    public string IdempotencyKey { get; private set; } = string.Empty;

    private Reminder() { }

    public static Reminder Schedule(Guid appointmentId, Guid patientId, ReminderChannel channel,
        int leadTimeMinutes, DateTimeOffset scheduledSendAtUtc)
    {
        if (leadTimeMinutes <= 0)
            throw new DomainException("reminder.lead.invalid", "Lead time minutes must be > 0.");
        return new Reminder
        {
            AppointmentId = appointmentId,
            PatientId = patientId,
            Channel = channel,
            LeadTimeMinutes = leadTimeMinutes,
            ScheduledSendAtUtc = scheduledSendAtUtc,
            Status = ReminderStatus.Pending,
            IdempotencyKey = $"{appointmentId:N}|{leadTimeMinutes}|{channel}"
        };
    }

    public void MarkSent(DateTimeOffset at)
    {
        Status = ReminderStatus.Sent;
        LastAttemptAtUtc = at;
        Attempts++;
    }

    public void MarkFailure(string error, DateTimeOffset at, int maxAttempts)
    {
        LastError = error;
        LastAttemptAtUtc = at;
        Attempts++;
        Status = Attempts >= maxAttempts ? ReminderStatus.DeadLettered : ReminderStatus.Failed;
    }

    public void Cancel() => Status = ReminderStatus.Cancelled;
}
