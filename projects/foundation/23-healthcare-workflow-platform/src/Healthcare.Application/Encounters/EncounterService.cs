using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Domain.Common;
using Healthcare.Domain.Encounters;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Encounters;

public sealed class EncounterService
{
    private readonly IAppDbContext _db;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    public EncounterService(IAppDbContext db, IClock clock, IAuditService audit)
    { _db = db; _clock = clock; _audit = audit; }

    public async Task<Encounter> OpenAsync(Guid appointmentId, bool requiresCoSign, string actorId,
        string actorRole, string correlationId, CancellationToken ct)
    {
        var appt = await _db.Appointments.FirstOrDefaultAsync(a => a.Id == appointmentId, ct)
            ?? throw new DomainException("appointment.not_found", "Appointment not found.");
        var existing = await _db.Encounters.FirstOrDefaultAsync(e => e.AppointmentId == appointmentId, ct);
        if (existing is not null) return existing;
        var encounter = Encounter.Open(appointmentId, appt.PatientId, appt.ClinicianId, _clock.UtcNow, requiresCoSign);
        _db.Encounters.Add(encounter);
        await _db.SaveChangesAsync(ct);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == appt.PatientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient?.ExternalId.Value, $"encounter:{encounter.Id}", "open",
            "encounter.open", correlationId, false, null, patient?.IsVip ?? false), ct);
        return encounter;
    }

    public async Task<ClinicalNote> AddNoteAsync(Guid encounterId, Guid authorId, string chiefComplaint,
        string observations, string assessment, string plan, string actorId, string actorRole,
        string correlationId, CancellationToken ct)
    {
        var encounter = await _db.Encounters
            .Include(e => e.Notes)
            .FirstOrDefaultAsync(e => e.Id == encounterId, ct)
            ?? throw new DomainException("encounter.not_found", "Encounter not found.");
        var note = encounter.AddNote(authorId, chiefComplaint, observations, assessment, plan, _clock.UtcNow);
        _db.ClinicalNotes.Add(note);
        await _db.SaveChangesAsync(ct);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == encounter.PatientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient?.ExternalId.Value, $"note:{note.Id}", "add",
            "encounter.note.add", correlationId, false, null, patient?.IsVip ?? false), ct);
        return note;
    }

    public async Task<ClinicalNote> AmendNoteAsync(Guid encounterId, Guid originalNoteId, Guid authorId,
        string chiefComplaint, string observations, string assessment, string plan, string reason,
        string actorId, string actorRole, string correlationId, CancellationToken ct)
    {
        var encounter = await _db.Encounters
            .Include(e => e.Notes)
            .FirstOrDefaultAsync(e => e.Id == encounterId, ct)
            ?? throw new DomainException("encounter.not_found", "Encounter not found.");
        var amendment = encounter.AmendNote(originalNoteId, authorId, chiefComplaint, observations,
            assessment, plan, reason, _clock.UtcNow);
        _db.ClinicalNotes.Add(amendment);
        await _db.SaveChangesAsync(ct);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == encounter.PatientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient?.ExternalId.Value, $"note:{amendment.Id}", "amend",
            "encounter.note.amend", correlationId, false, null, patient?.IsVip ?? false), ct);
        return amendment;
    }

    public async Task<VitalReading> RecordVitalAsync(Guid encounterId, string kind, decimal value, string unit,
        string actorId, string actorRole, string correlationId, CancellationToken ct)
    {
        var encounter = await _db.Encounters
            .Include(e => e.Vitals)
            .FirstOrDefaultAsync(e => e.Id == encounterId, ct)
            ?? throw new DomainException("encounter.not_found", "Encounter not found.");
        encounter.AddVital(kind, value, unit, _clock.UtcNow);
        var recorded = encounter.Vitals.Last();
        _db.VitalReadings.Add(recorded);
        await _db.SaveChangesAsync(ct);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == encounter.PatientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient?.ExternalId.Value, $"vital:{recorded.Id}", "record",
            "encounter.vital.record", correlationId, false, null, patient?.IsVip ?? false), ct);
        return recorded;
    }

    public async Task CoSignAsync(Guid encounterId, Guid supervisorClinicianId, string actorId,
        string actorRole, string correlationId, CancellationToken ct)
    {
        var encounter = await _db.Encounters.FirstOrDefaultAsync(e => e.Id == encounterId, ct)
            ?? throw new DomainException("encounter.not_found", "Encounter not found.");
        encounter.CoSign(supervisorClinicianId, _clock.UtcNow);
        await _db.SaveChangesAsync(ct);
        var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == encounter.PatientId, ct);
        await _audit.RecordAsync(new AuditRequest(
            Healthcare.Domain.Audit.AuditKind.PatientDataWrite, actorId, actorRole,
            patient?.ExternalId.Value, $"encounter:{encounter.Id}", "cosign",
            "encounter.cosign", correlationId, false, null, patient?.IsVip ?? false), ct);
    }
}
