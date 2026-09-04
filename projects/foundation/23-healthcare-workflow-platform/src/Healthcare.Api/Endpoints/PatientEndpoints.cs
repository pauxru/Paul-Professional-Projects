using Healthcare.Application.Abstractions;
using Healthcare.Application.Access;
using Healthcare.Application.Appointments;
using Healthcare.Application.Patients;
using Healthcare.Domain.Audit;
using Healthcare.Domain.Common;
using Healthcare.Domain.Patients;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Api.Endpoints;

public static class PatientEndpoints
{
    public static void MapPatientEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/patients").WithTags("Patients").RequireAuthorization();

        // List — receptionists allowed. Returns demographic/scheduling fields only; no clinical data.
        g.MapGet("/", async Task<Ok<object>> (
            IAppDbContext db, ICurrentUser user, IAuditService audit,
            int page = 1, int pageSize = 25, CancellationToken ct = default) =>
        {
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 1, 100);
            var q = db.Patients.OrderBy(p => p.FamilyName).ThenBy(p => p.GivenName);
            var total = await q.CountAsync(ct);
            var items = await q.Skip((page - 1) * pageSize).Take(pageSize)
                .Select(p => new { p.Id, ExternalId = p.ExternalId, p.GivenName, p.FamilyName, p.DateOfBirth, p.PreferredChannel, p.OptedOutOfReminders })
                .ToListAsync(ct);
            return TypedResults.Ok<object>(new { items, page, pageSize, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)pageSize) });
        });

        // Get demographic — receptionists allowed.
        g.MapGet("/{id:guid}", async Task<Results<Ok<object>, NotFound>> (
            Guid id, IAppDbContext db, IAuditService audit, ICurrentUser user, CancellationToken ct) =>
        {
            var p = await db.Patients.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return TypedResults.NotFound();
            await audit.RecordAsync(new AuditRequest(
                AuditKind.PatientDataRead, user.UserId, user.Roles.FirstOrDefault() ?? "unknown",
                p.ExternalId.Value, $"patient:{p.Id}", "read_demographic", "patient.get",
                user.CorrelationId, false, null, p.IsVip), ct);
            return TypedResults.Ok<object>(new
            {
                p.Id,
                externalId = p.ExternalId.Value,
                p.GivenName, p.FamilyName, p.DateOfBirth, p.Sex, p.PhoneE164, p.Email,
                preferredChannel = p.PreferredChannel.ToString(),
                p.OptedOutOfReminders, p.RequiresConfirmation, p.IsVip,
                consents = p.Consents.Select(c => new { kind = c.Kind.ToString(), c.GrantedAt, c.RevokedAt })
            });
        });

        // Clinical record — ABAC-guarded.
        g.MapGet("/{id:guid}/clinical", async Task<Results<Ok<object>, ForbidHttpResult, NotFound>> (
            Guid id, IAppDbContext db, IClinicalAccessGuard guard, IAuditService audit,
            ICurrentUser user, CancellationToken ct) =>
        {
            var p = await db.Patients.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return TypedResults.NotFound();
            var decision = await guard.AuthorizeReadAsync(id, ct);
            if (!decision.Granted) return TypedResults.Forbid();

            var encounters = await db.Encounters
                .Where(e => e.PatientId == id).OrderByDescending(e => e.OpenedAtUtc).Take(50).ToListAsync(ct);
            var notes = await db.ClinicalNotes
                .Where(n => encounters.Select(e => e.Id).Contains(n.EncounterId))
                .OrderBy(n => n.CreatedAtUtc).ToListAsync(ct);
            var vitals = await db.VitalReadings
                .Where(v => encounters.Select(e => e.Id).Contains(v.EncounterId))
                .OrderBy(v => v.RecordedAtUtc).ToListAsync(ct);

            await audit.RecordAsync(new AuditRequest(
                AuditKind.PatientDataRead, user.UserId, user.Roles.FirstOrDefault() ?? "unknown",
                p.ExternalId.Value, $"patient:{p.Id}/clinical", "read_clinical",
                "patient.clinical.read", user.CorrelationId, decision.BreakGlass, user.BreakGlassJustification, p.IsVip), ct);

            return TypedResults.Ok<object>(new
            {
                patientId = p.Id,
                externalId = p.ExternalId.Value,
                encounters = encounters.Select(e => new { e.Id, e.OpenedAtUtc, e.ClosedAtUtc, e.RequiresCoSign, e.CoSignedAtUtc }),
                notes = notes.Select(n => new { n.Id, n.EncounterId, n.RootNoteId, n.Version, n.IsAmendment, n.ChiefComplaint, n.Assessment, n.Plan, n.AmendmentReason, n.CreatedAtUtc }),
                vitals = vitals.Select(v => new { v.Id, v.EncounterId, v.Kind, v.Value, v.Unit, v.RecordedAtUtc }),
                access = new { granted = decision.Granted, breakGlass = decision.BreakGlass, reason = decision.Reason }
            });
        }).RequireAuthorization(Policies.CanReadClinicalRecord);

        // Register — receptionists / clinicians / lead / admin.
        g.MapPost("/", async Task<Results<Created<object>, ProblemHttpResult>> (
            RegisterPatientRequest req, PatientRegistrationService svc, ICurrentUser user, CancellationToken ct) =>
        {
            try
            {
                var patient = await svc.RegisterAsync(new RegisterPatientCommand(
                    req.GivenName, req.FamilyName, req.DateOfBirth, req.Sex, req.PhoneE164, req.Email,
                    Enum.Parse<ContactChannel>(req.PreferredChannel, ignoreCase: true), req.RegisteredClinicId,
                    req.AllowDuplicate),
                    user.UserId, user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.Created($"/api/v1/patients/{patient.Id}",
                    (object)new { patient.Id, externalId = patient.ExternalId.Value });
            }
            catch (DomainException ex)
            {
                return TypedResults.Problem(detail: ex.Message, statusCode: 422, title: ex.Code);
            }
        }).RequireAuthorization(Policies.CanManageWorkflow);

        // Duplicate check (fuzzy).
        g.MapGet("/duplicate-check", async Task<Ok<object>> (
            string givenName, string familyName, DateOnly dateOfBirth, string phone,
            PatientRegistrationService svc, CancellationToken ct) =>
        {
            var matches = await svc.FindDuplicatesAsync(givenName, familyName, dateOfBirth, phone, ct);
            return TypedResults.Ok<object>(new { matches });
        });

        // Opt-out endpoint for reminders.
        g.MapPost("/{id:guid}/opt-out-reminders", async Task<Results<NoContent, NotFound>> (
            Guid id, IAppDbContext db, CancellationToken ct) =>
        {
            var p = await db.Patients.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (p is null) return TypedResults.NotFound();
            p.OptOutOfReminders();
            await db.SaveChangesAsync(ct);
            return TypedResults.NoContent();
        }).RequireAuthorization(Policies.CanManageWorkflow);
    }

    public sealed record RegisterPatientRequest(
        string GivenName, string FamilyName, DateOnly DateOfBirth, string Sex, string PhoneE164,
        string? Email, string PreferredChannel, Guid RegisteredClinicId, bool AllowDuplicate = false);
}
