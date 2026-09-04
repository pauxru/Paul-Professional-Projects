using Healthcare.Application.Abstractions;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Appointments;

public sealed record BookAppointmentCommand(
    Guid PatientId,
    Guid ClinicianId,
    Guid FacilityId,
    Guid RoomId,
    Guid AppointmentTypeId,
    DateTimeOffset StartUtc);

public sealed class AppointmentBookingService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IAuditService _audit;

    public AppointmentBookingService(IAppDbContext db, IClock clock, IAuditService audit)
    {
        _db = db;
        _clock = clock;
        _audit = audit;
    }

    public async Task<Appointment> BookAsync(BookAppointmentCommand cmd, string actorId, string actorRole,
        string correlationId, CancellationToken ct)
    {
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == cmd.PatientId, ct)
            ?? throw new DomainException("patient.not_found", "Patient not found.");
        var apptType = await _db.AppointmentTypes.FirstOrDefaultAsync(a => a.Id == cmd.AppointmentTypeId, ct)
            ?? throw new DomainException("apptype.not_found", "Appointment type not found.");
        var endUtc = cmd.StartUtc.AddMinutes(apptType.DurationMinutes);
        var buffer = TimeSpan.FromMinutes(apptType.BufferMinutes);

        // Overlap check — conflict prevention. The concurrency-safe check is the DB unique index
        // + optimistic concurrency; this is the first-line application guard so most contention
        // never reaches the constraint.
        var conflict = await _db.Appointments.AnyAsync(a =>
            a.Status != AppointmentStatus.Cancelled &&
            a.Status != AppointmentStatus.NoShow &&
            (
                (a.ClinicianId == cmd.ClinicianId && a.StartUtc < endUtc && cmd.StartUtc < a.EndUtc) ||
                (a.RoomId == cmd.RoomId && a.StartUtc < endUtc && cmd.StartUtc < a.EndUtc)
            ), ct);
        if (conflict && !apptType.AllowsOverbooking)
            throw new DomainException("appointment.conflict", "Requested slot conflicts with an existing appointment.");

        var appt = Appointment.Book(cmd.PatientId, cmd.ClinicianId, cmd.FacilityId, cmd.RoomId,
            cmd.AppointmentTypeId, cmd.StartUtc, endUtc);
        _db.Appointments.Add(appt);

        // Confirmation requirement — if patient flag is set, appointment stays Booked (not auto-confirmed).
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException ex) when (IsConcurrencyConflict(ex))
        {
            // Concurrent booking lost the race at the DB unique constraint.
            throw new DomainException("appointment.conflict", "Slot was booked by another concurrent request.");
        }

        await _audit.RecordAsync(new AuditRequest(
            Kind: Healthcare.Domain.Audit.AuditKind.PatientDataWrite,
            ActorId: actorId,
            ActorRole: actorRole,
            PatientId: patient.ExternalId.Value,
            Resource: $"appointment:{appt.Id}",
            Action: "book",
            Purpose: "appointment.book",
            CorrelationId: correlationId,
            BreakGlass: false,
            Justification: null,
            VipPatient: patient.IsVip), ct);

        return appt;
    }

    public async Task RescheduleAsync(Guid appointmentId, DateTimeOffset newStartUtc, string actorId,
        string actorRole, string correlationId, CancellationToken ct)
    {
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, ct)
            ?? throw new DomainException("appointment.not_found", "Appointment not found.");
        var apptType = await _db.AppointmentTypes.FirstOrDefaultAsync(a => a.Id == appt.AppointmentTypeId, ct)
            ?? throw new DomainException("apptype.not_found", "Appointment type not found.");
        var newEnd = newStartUtc.AddMinutes(apptType.DurationMinutes);
        var conflict = await _db.Appointments.AnyAsync(a =>
            a.Id != appointmentId &&
            a.Status != AppointmentStatus.Cancelled && a.Status != AppointmentStatus.NoShow &&
            (
                (a.ClinicianId == appt.ClinicianId && a.StartUtc < newEnd && newStartUtc < a.EndUtc) ||
                (a.RoomId == appt.RoomId && a.StartUtc < newEnd && newStartUtc < a.EndUtc)
            ), ct);
        if (conflict) throw new DomainException("appointment.conflict", "Reschedule target conflicts with existing appointment.");
        appt.Reschedule(newStartUtc, newEnd);
        await _db.SaveChangesAsync(ct);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == appt.PatientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient?.ExternalId.Value, $"appointment:{appt.Id}", "reschedule",
            "appointment.reschedule", correlationId, false, null, patient?.IsVip ?? false), ct);
    }

    public async Task CancelAsync(Guid appointmentId, CancellationReason reason, string? notes,
        string actorId, string actorRole, string correlationId, CancellationToken ct)
    {
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, ct)
            ?? throw new DomainException("appointment.not_found", "Appointment not found.");
        appt.Cancel(reason, notes, _clock.UtcNow);
        await _db.SaveChangesAsync(ct);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == appt.PatientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient?.ExternalId.Value, $"appointment:{appt.Id}", "cancel",
            "appointment.cancel", correlationId, false, null, patient?.IsVip ?? false), ct);
    }

    private static bool IsConcurrencyConflict(Exception? ex)
    {
        while (ex is not null)
        {
            var msg = ex.Message ?? "";
            if (msg.IndexOf("UNIQUE", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("constraint failed", StringComparison.OrdinalIgnoreCase) >= 0
                || msg.IndexOf("SQLITE_CONSTRAINT", StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            // SqliteException error codes: 19 = SQLITE_CONSTRAINT.
            var errCodeProp = ex.GetType().GetProperty("SqliteErrorCode");
            if (errCodeProp is not null)
            {
                var code = errCodeProp.GetValue(ex) as int?;
                if (code == 19) return true;
            }
            ex = ex.InnerException;
        }
        return false;
    }

    private static bool IsUniqueConstraint(Exception? ex) => IsConcurrencyConflict(ex);
}
