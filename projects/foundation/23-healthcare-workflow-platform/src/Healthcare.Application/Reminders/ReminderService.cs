using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Common;
using Healthcare.Domain.Patients;
using Healthcare.Domain.Reminders;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Reminders;

public sealed record ReminderPolicy(int[] LeadTimesMinutes, int MaxAttempts = 3);

public sealed class ReminderService
{
    public static readonly ReminderPolicy DefaultPolicy = new(new[] { 48 * 60, 2 * 60 });
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IReminderChannel _sms;
    private readonly IReminderChannel _email;
    private readonly IAuditService _audit;

    public ReminderService(IAppDbContext db, IClock clock, IEnumerable<IReminderChannel> channels, IAuditService audit)
    {
        _db = db;
        _clock = clock;
        _sms = channels.First(c => c.Name.Equals("sms", StringComparison.OrdinalIgnoreCase));
        _email = channels.First(c => c.Name.Equals("email", StringComparison.OrdinalIgnoreCase));
        _audit = audit;
    }

    /// <summary>
    /// Idempotently schedules reminders for a given appointment following the policy. Existing
    /// reminders with the same (appointment, lead, channel) key are not duplicated.
    /// </summary>
    public async Task<int> ScheduleAsync(Guid appointmentId, ReminderPolicy? policy, CancellationToken ct)
    {
        var pol = policy ?? DefaultPolicy;
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, ct)
            ?? throw new DomainException("appointment.not_found", "Appointment not found.");
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == appt.PatientId, ct)
            ?? throw new DomainException("patient.not_found", "Patient not found.");
        if (patient.OptedOutOfReminders) return 0;

        int added = 0;
        var chosenChannel = patient.PreferredChannel switch
        {
            ContactChannel.Sms => ReminderChannel.Sms,
            ContactChannel.Email => ReminderChannel.Email,
            _ => ReminderChannel.Sms
        };
        var existing = await _db.Reminders.Where(r => r.AppointmentId == appointmentId).ToListAsync(ct);
        foreach (var lead in pol.LeadTimesMinutes)
        {
            var sendAt = appt.StartUtc.AddMinutes(-lead);
            var key = $"{appointmentId:N}|{lead}|{chosenChannel}";
            if (existing.Any(r => r.IdempotencyKey == key)) continue;
            var reminder = Reminder.Schedule(appointmentId, patient.Id, chosenChannel, lead, sendAt);
            _db.Reminders.Add(reminder);
            added++;
        }
        await _db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// Dispatches all due reminders (ScheduledSendAt &lt;= now). Called by a hosted background loop
    /// in production; called directly from tests for determinism.
    /// </summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        var now = _clock.UtcNow;
        var due = await _db.Reminders
            .Where(r => r.Status == ReminderStatus.Pending && r.ScheduledSendAtUtc <= now)
            .ToListAsync(ct);
        int dispatched = 0;
        foreach (var r in due)
        {
            var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == r.PatientId, ct);
            if (patient is null) { r.Cancel(); continue; }
            if (patient.OptedOutOfReminders) { r.Cancel(); continue; }
            var body = $"[Nairobi Demo Clinic (fictional)] Reminder: appointment at {(r.ScheduledSendAtUtc.AddMinutes(r.LeadTimeMinutes)):u}. Reply YES to confirm.";
            var recipient = r.Channel == ReminderChannel.Sms ? patient.PhoneE164 : (patient.Email ?? patient.PhoneE164);
            IReminderChannel channel = r.Channel == ReminderChannel.Sms ? _sms : _email;
            var msg = new ReminderMessage(r.Id, r.AppointmentId, r.PatientId, recipient, body, r.IdempotencyKey);
            var result = await channel.SendAsync(msg, ct);
            if (result.Success) { r.MarkSent(now); dispatched++; }
            else r.MarkFailure(result.Error ?? "unknown", now, ReminderService.DefaultPolicy.MaxAttempts);
            await _audit.RecordAsync(new AuditRequest(
                Healthcare.Domain.Audit.AuditKind.ReminderDispatch,
                ActorId: "system.reminder", ActorRole: "system",
                PatientId: patient.ExternalId.Value,
                Resource: $"reminder:{r.Id}",
                Action: result.Success ? "sent" : "failed",
                Purpose: "reminder.dispatch",
                CorrelationId: r.IdempotencyKey,
                BreakGlass: false, Justification: null, VipPatient: patient.IsVip), ct);
        }
        await _db.SaveChangesAsync(ct);
        return dispatched;
    }

    /// <summary>
    /// Applies a confirmation reply from a patient. Sets appointment status to Confirmed.
    /// </summary>
    public async Task<bool> ApplyConfirmationAsync(Guid appointmentId, CancellationToken ct)
    {
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
        if (appt is null) return false;
        if (appt.Status != AppointmentStatus.Booked) return false;
        appt.Confirm(_clock.UtcNow);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
