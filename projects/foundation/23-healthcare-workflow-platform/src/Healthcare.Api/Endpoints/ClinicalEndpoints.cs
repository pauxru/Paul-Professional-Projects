using Healthcare.Application.Abstractions;
using Healthcare.Application.Access;
using Healthcare.Application.Appointments;
using Healthcare.Application.Encounters;
using Healthcare.Application.Referrals;
using Healthcare.Application.Reminders;
using Healthcare.Application.Reports;
using Healthcare.Application.Waitlist;
using Healthcare.Domain.Audit;
using Healthcare.Domain.Common;
using Healthcare.Domain.Referrals;
using Healthcare.Domain.Waitlist;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Api.Endpoints;

public static class EncounterEndpoints
{
    public static void MapEncounterEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/encounters").WithTags("Encounters").RequireAuthorization();

        g.MapPost("/", async Task<Results<Created<object>, ProblemHttpResult>> (
            OpenEncounterRequest req, EncounterService svc, ICurrentUser user, CancellationToken ct) =>
        {
            try
            {
                var e = await svc.OpenAsync(req.AppointmentId, req.RequiresCoSign, user.UserId,
                    user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.Created($"/api/v1/encounters/{e.Id}", (object)new { e.Id, e.OpenedAtUtc });
            }
            catch (DomainException ex) { return TypedResults.Problem(ex.Message, statusCode: 422, title: ex.Code); }
        }).RequireAuthorization(Policies.CanManageWorkflow);

        g.MapPost("/{id:guid}/notes", async Task<Results<Created<object>, ProblemHttpResult>> (
            Guid id, AddNoteRequest req, EncounterService svc, ICurrentUser user, IClinicalAccessGuard guard,
            IAppDbContext db, CancellationToken ct) =>
        {
            var encounter = await db.Encounters.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (encounter is null) return TypedResults.Problem("Encounter not found", statusCode: 404, title: "encounter.not_found");
            var decision = await guard.AuthorizeReadAsync(encounter.PatientId, ct);
            if (!decision.Granted) return TypedResults.Problem("No care relationship", statusCode: 403, title: "clinical_access.denied");
            try
            {
                var authorId = user.ClinicianId ?? Guid.Empty;
                var note = await svc.AddNoteAsync(id, authorId, req.ChiefComplaint, req.Observations,
                    req.Assessment, req.Plan, user.UserId, user.Roles.FirstOrDefault() ?? "unknown",
                    user.CorrelationId, ct);
                return TypedResults.Created($"/api/v1/encounters/{id}/notes/{note.Id}",
                    (object)new { note.Id, note.Version, note.RootNoteId, note.CreatedAtUtc });
            }
            catch (DomainException ex) { return TypedResults.Problem(ex.Message, statusCode: 422, title: ex.Code); }
        }).RequireAuthorization(Policies.CanWriteClinicalNote);

        g.MapPost("/{id:guid}/notes/{noteId:guid}/amend", async Task<Results<Created<object>, ProblemHttpResult>> (
            Guid id, Guid noteId, AddNoteWithReasonRequest req, EncounterService svc, ICurrentUser user,
            IClinicalAccessGuard guard, IAppDbContext db, CancellationToken ct) =>
        {
            var encounter = await db.Encounters.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (encounter is null) return TypedResults.Problem("Encounter not found", statusCode: 404, title: "encounter.not_found");
            var decision = await guard.AuthorizeReadAsync(encounter.PatientId, ct);
            if (!decision.Granted) return TypedResults.Problem("No care relationship", statusCode: 403, title: "clinical_access.denied");
            try
            {
                var authorId = user.ClinicianId ?? Guid.Empty;
                var amend = await svc.AmendNoteAsync(id, noteId, authorId, req.ChiefComplaint, req.Observations,
                    req.Assessment, req.Plan, req.AmendmentReason, user.UserId,
                    user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.Created($"/api/v1/encounters/{id}/notes/{amend.Id}",
                    (object)new { amend.Id, amend.Version, amend.RootNoteId, amend.CreatedAtUtc });
            }
            catch (DomainException ex) { return TypedResults.Problem(ex.Message, statusCode: 422, title: ex.Code); }
        }).RequireAuthorization(Policies.CanWriteClinicalNote);

        g.MapPost("/{id:guid}/vitals", async Task<Results<Created<object>, ProblemHttpResult>> (
            Guid id, VitalRequest req, EncounterService svc, ICurrentUser user, IClinicalAccessGuard guard,
            IAppDbContext db, CancellationToken ct) =>
        {
            var encounter = await db.Encounters.FirstOrDefaultAsync(e => e.Id == id, ct);
            if (encounter is null) return TypedResults.Problem("Encounter not found", statusCode: 404, title: "encounter.not_found");
            var decision = await guard.AuthorizeReadAsync(encounter.PatientId, ct);
            if (!decision.Granted) return TypedResults.Problem("No care relationship", statusCode: 403, title: "clinical_access.denied");
            try
            {
                var v = await svc.RecordVitalAsync(id, req.Kind, req.Value, req.Unit, user.UserId,
                    user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.Created($"/api/v1/encounters/{id}/vitals/{v.Id}",
                    (object)new { v.Id, v.Kind, v.Value, v.Unit });
            }
            catch (DomainException ex) { return TypedResults.Problem(ex.Message, statusCode: 422, title: ex.Code); }
        }).RequireAuthorization(Policies.CanWriteClinicalNote);

        g.MapPost("/{id:guid}/cosign", async Task<Results<NoContent, ProblemHttpResult>> (
            Guid id, CoSignRequest req, EncounterService svc, ICurrentUser user, CancellationToken ct) =>
        {
            try
            {
                await svc.CoSignAsync(id, req.SupervisorClinicianId, user.UserId,
                    user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.NoContent();
            }
            catch (DomainException ex) { return TypedResults.Problem(ex.Message, statusCode: 422, title: ex.Code); }
        }).RequireAuthorization(Policies.CanWriteClinicalNote);
    }

    public sealed record OpenEncounterRequest(Guid AppointmentId, bool RequiresCoSign);
    public sealed record AddNoteRequest(string ChiefComplaint, string Observations, string Assessment, string Plan);
    public sealed record AddNoteWithReasonRequest(string ChiefComplaint, string Observations, string Assessment,
        string Plan, string AmendmentReason);
    public sealed record VitalRequest(string Kind, decimal Value, string Unit);
    public sealed record CoSignRequest(Guid SupervisorClinicianId);
}

public static class ReferralEndpoints
{
    public static void MapReferralEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/referrals").WithTags("Referrals").RequireAuthorization();

        g.MapGet("/", async (IAppDbContext db, CancellationToken ct) =>
        {
            var items = await db.Referrals.OrderByDescending(r => r.CreatedAtUtc).Take(200)
                .Select(r => new { r.Id, r.PatientId, r.Priority, r.Speciality, status = r.Status.ToString(),
                    r.CreatedAtUtc, r.SlaDueUtc, r.SlaBreached }).ToListAsync(ct);
            return TypedResults.Ok(items);
        });

        g.MapPost("/", async Task<Results<Created<object>, ProblemHttpResult>> (
            CreateReferralRequest req, ReferralService svc, ICurrentUser user, CancellationToken ct) =>
        {
            try
            {
                var r = await svc.CreateAsync(new CreateReferralCommand(
                    req.PatientId, req.ReferringClinicianId, req.DestinationFacilityId, req.ExternalDestination ?? "",
                    req.Speciality, Enum.Parse<ReferralPriority>(req.Priority, ignoreCase: true), req.ReasonForReferral),
                    user.UserId, user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.Created($"/api/v1/referrals/{r.Id}",
                    (object)new { r.Id, r.SlaDueUtc, r.Priority });
            }
            catch (DomainException ex) { return TypedResults.Problem(ex.Message, statusCode: 422, title: ex.Code); }
        }).RequireAuthorization(Policies.CanWriteClinicalNote);

        g.MapPost("/{id:guid}/actions/{action}", async Task<Results<NoContent, ProblemHttpResult>> (
            Guid id, string action, ReferralTransitionRequest? req, ReferralService svc, ICurrentUser user, CancellationToken ct) =>
        {
            try
            {
                await svc.TransitionAsync(id, action, req?.Reason, user.UserId,
                    user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.NoContent();
            }
            catch (DomainException ex) { return TypedResults.Problem(ex.Message, statusCode: 422, title: ex.Code); }
        }).RequireAuthorization(Policies.CanWriteClinicalNote);
    }

    public sealed record CreateReferralRequest(Guid PatientId, Guid ReferringClinicianId,
        Guid? DestinationFacilityId, string? ExternalDestination, string Speciality, string Priority,
        string ReasonForReferral);
    public sealed record ReferralTransitionRequest(string? Reason);
}

public static class WaitlistEndpoints
{
    public static void MapWaitlistEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/waitlist").WithTags("Waitlist").RequireAuthorization();

        g.MapGet("/", async (IAppDbContext db, Guid? facilityId, CancellationToken ct) =>
        {
            IQueryable<WaitlistEntry> q = db.WaitlistEntries;
            if (facilityId is not null) q = q.Where(w => w.FacilityId == facilityId);
            var items = await q.OrderByDescending(w => w.Priority).ThenBy(w => w.CreatedAtUtc).Take(100)
                .Select(w => new { w.Id, w.PatientId, w.FacilityId, w.AppointmentTypeId, priority = w.Priority.ToString(),
                    status = w.Status.ToString(), w.CreatedAtUtc, w.OfferExpiresAtUtc }).ToListAsync(ct);
            return TypedResults.Ok(items);
        });

        g.MapPost("/", async Task<Results<Created<object>, ProblemHttpResult>> (
            JoinWaitlistRequest req, WaitlistService svc, ICurrentUser user, CancellationToken ct) =>
        {
            try
            {
                var e = await svc.JoinAsync(req.PatientId, req.FacilityId, req.AppointmentTypeId,
                    Enum.Parse<WaitlistPriority>(req.Priority, ignoreCase: true),
                    user.UserId, user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.Created($"/api/v1/waitlist/{e.Id}", (object)new { e.Id });
            }
            catch (DomainException ex) { return TypedResults.Problem(ex.Message, statusCode: 422, title: ex.Code); }
        }).RequireAuthorization(Policies.CanManageWorkflow);
    }

    public sealed record JoinWaitlistRequest(Guid PatientId, Guid FacilityId, Guid AppointmentTypeId, string Priority);
}

public static class ReminderEndpoints
{
    public static void MapReminderEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/reminders").WithTags("Reminders").RequireAuthorization();

        g.MapPost("/schedule/{appointmentId:guid}", async Task<Results<Ok<object>, ProblemHttpResult>> (
            Guid appointmentId, ReminderService svc, CancellationToken ct) =>
        {
            try
            {
                var added = await svc.ScheduleAsync(appointmentId, null, ct);
                return TypedResults.Ok<object>(new { scheduled = added });
            }
            catch (DomainException ex) { return TypedResults.Problem(ex.Message, statusCode: 422, title: ex.Code); }
        }).RequireAuthorization(Policies.CanManageWorkflow);

        g.MapPost("/dispatch", async (ReminderService svc, CancellationToken ct) =>
        {
            var count = await svc.DispatchDueAsync(ct);
            return TypedResults.Ok(new { dispatched = count });
        }).RequireAuthorization(Policies.CanManageWorkflow);

        g.MapPost("/confirm/{appointmentId:guid}", async Task<Results<NoContent, NotFound>> (
            Guid appointmentId, ReminderService svc, CancellationToken ct) =>
        {
            var applied = await svc.ApplyConfirmationAsync(appointmentId, ct);
            return applied ? TypedResults.NoContent() : TypedResults.NotFound();
        }).RequireAuthorization(Policies.CanManageWorkflow);
    }
}

public static class ReportsEndpoints
{
    public static void MapReportsEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/reports").WithTags("Reports").RequireAuthorization();
        g.MapGet("/clinic-board", async (Guid facilityId, DateOnly? date, ReportsService svc, IClock clock, CancellationToken ct) =>
        {
            var d = date ?? DateOnly.FromDateTime(clock.UtcNow.Date);
            var rows = await svc.ClinicBoardAsync(facilityId, d, ct);
            return TypedResults.Ok(rows);
        });
        g.MapGet("/utilisation", async (Guid facilityId, DateOnly fromDate, DateOnly toDate,
            ReportsService svc, CancellationToken ct) =>
            TypedResults.Ok(await svc.UtilisationAsync(facilityId, fromDate, toDate, ct)));
        g.MapGet("/dna", async (Guid facilityId, DateOnly fromDate, DateOnly toDate,
            ReportsService svc, CancellationToken ct) =>
            TypedResults.Ok(await svc.DnaStatsAsync(facilityId, fromDate, toDate, ct)));
        g.MapGet("/access-anomalies", async (DateTimeOffset? sinceUtc, ReportsService svc, CancellationToken ct) =>
            TypedResults.Ok(await svc.AccessAnomaliesAsync(sinceUtc, ct)))
            .RequireAuthorization(Policies.CanViewAudit);
        g.MapGet("/referral-breaches", async (IAppDbContext db, CancellationToken ct) =>
        {
            var items = await db.Referrals.Where(r => r.SlaBreached &&
                (r.Status == ReferralStatus.Draft || r.Status == ReferralStatus.Submitted || r.Status == ReferralStatus.Triaged))
                .OrderBy(r => r.SlaDueUtc)
                .Select(r => new { r.Id, r.PatientId, priority = r.Priority.ToString(), status = r.Status.ToString(),
                    r.SlaDueUtc, r.CreatedAtUtc }).ToListAsync(ct);
            return TypedResults.Ok(items);
        });
    }
}

public static class AuditEndpoints
{
    public static void MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/audit").WithTags("Audit").RequireAuthorization(Policies.CanViewAudit);
        g.MapGet("/", async (IAuditService svc, DateTimeOffset? fromUtc, DateTimeOffset? toUtc,
            string? actorId, string? patientId, string? kind, CancellationToken ct) =>
        {
            AuditKind? k = null;
            if (!string.IsNullOrWhiteSpace(kind) && Enum.TryParse<AuditKind>(kind, ignoreCase: true, out var parsed))
                k = parsed;
            var items = await svc.QueryAsync(fromUtc, toUtc, actorId, patientId, k, ct);
            return TypedResults.Ok(items.Select(a => new
            {
                a.Id, kind = a.Kind.ToString(), a.ActorId, a.ActorRole, a.PatientId, a.Resource, a.Action,
                a.Purpose, a.CorrelationId, a.BreakGlass, a.Justification, a.OccurredAtUtc,
                a.OutOfHours, a.VipPatient
            }));
        });
    }
}
