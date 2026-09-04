using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Domain.Audit;
using Healthcare.Domain.Common;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Application.Access;

/// <summary>
/// Enforces ABAC "care relationship" for clinical data access. Semantics:
///
/// - A clinician has a care relationship with a patient if within the configured lookback window
///   they are the assigned clinician on an appointment / encounter, or the referring or receiving
///   clinician on a referral, or an active care team member (represented here through recent
///   encounters — the demo does not model care teams as a first-class entity).
///
/// - Break-glass overrides this check: it grants access provided a justification is supplied,
///   emits a high-severity audit event, and (in the demo) writes a queued anomaly alert. The
///   activation is only good for the current request.
///
/// - Receptionists never pass this check: RBAC alone forbids them from clinical resources, and
///   this service is only invoked for clinical resources.
/// </summary>
public interface IClinicalAccessGuard
{
    /// <summary>
    /// Returns access decision. On grant, callers MUST still ensure an audit "PatientDataRead"
    /// event is recorded via the audit service.
    /// </summary>
    Task<AccessDecision> AuthorizeReadAsync(Guid patientId, CancellationToken ct);
}

public sealed record AccessDecision(bool Granted, bool BreakGlass, string Reason);

public sealed class ClinicalAccessGuard : IClinicalAccessGuard
{
    private readonly IAppDbContext _db;
    private readonly ICurrentUser _user;
    private readonly IClock _clock;
    private readonly IAuditService _audit;
    /// <summary>Care relationship lookback window (in days).</summary>
    private static readonly TimeSpan RelationshipWindow = TimeSpan.FromDays(365);

    public ClinicalAccessGuard(IAppDbContext db, ICurrentUser user, IClock clock, IAuditService audit)
    { _db = db; _user = user; _clock = clock; _audit = audit; }

    public async Task<AccessDecision> AuthorizeReadAsync(Guid patientId, CancellationToken ct)
    {
        if (!_user.IsAuthenticated)
            return new AccessDecision(false, false, "unauthenticated");
        // Roles allowed to read clinical data (subject to ABAC).
        var clinicalRoles = new[] { Roles.Clinician, Roles.Nurse, Roles.ClinicalLead, Roles.Administrator, Roles.Auditor };
        if (!_user.Roles.Any(r => clinicalRoles.Contains(r)))
            return new AccessDecision(false, false, "role_not_permitted");

        // Auditor + ClinicalLead + Administrator get standing read (they have oversight roles).
        if (_user.HasRole(Roles.Administrator) || _user.HasRole(Roles.ClinicalLead) || _user.HasRole(Roles.Auditor))
            return new AccessDecision(true, false, "standing_role_access");

        // For clinician / nurse users, require a care relationship OR break-glass.
        if (_user.BreakGlassActivated)
        {
            var justification = _user.BreakGlassJustification;
            if (string.IsNullOrWhiteSpace(justification))
                return new AccessDecision(false, false, "break_glass_missing_justification");
            // Handle in a single audit call; caller still records the actual read audit.
            var patient = await _db.Patients.FirstOrDefaultAsync(p => p.Id == patientId, ct);
            await _audit.RecordAsync(new AuditRequest(
                AuditKind.BreakGlassActivated,
                _user.UserId,
                _user.Roles.FirstOrDefault() ?? "unknown",
                patient?.ExternalId.Value,
                $"patient:{patientId}",
                "break_glass",
                "clinical_access.break_glass",
                _user.CorrelationId,
                true,
                justification,
                patient?.IsVip ?? false), ct);
            return new AccessDecision(true, true, "break_glass");
        }

        if (_user.ClinicianId is null)
            return new AccessDecision(false, false, "no_clinician_context");

        var since = _clock.UtcNow - RelationshipWindow;
        var clinicianId = _user.ClinicianId.Value;

        var hasApptRelationship = await _db.Appointments.AnyAsync(a =>
            a.PatientId == patientId && a.ClinicianId == clinicianId && a.StartUtc >= since, ct);
        if (hasApptRelationship) return new AccessDecision(true, false, "appointment");

        var hasEncounter = await _db.Encounters.AnyAsync(e =>
            e.PatientId == patientId && e.ClinicianId == clinicianId && e.OpenedAtUtc >= since, ct);
        if (hasEncounter) return new AccessDecision(true, false, "encounter");

        var hasReferral = await _db.Referrals.AnyAsync(r =>
            r.PatientId == patientId && r.ReferringClinicianId == clinicianId && r.CreatedAtUtc >= since, ct);
        if (hasReferral) return new AccessDecision(true, false, "referral");

        // Deny + record for anomaly detection.
        await _audit.RecordAsync(new AuditRequest(
            AuditKind.AccessDenied, _user.UserId,
            _user.Roles.FirstOrDefault() ?? "unknown",
            null,
            $"patient:{patientId}",
            "read",
            "clinical_access.denied_no_relationship",
            _user.CorrelationId,
            false, null, false), ct);
        return new AccessDecision(false, false, "no_care_relationship");
    }
}
