using Healthcare.Application.Abstractions;
using Healthcare.Application.Appointments;
using Healthcare.Application.Availability;
using Healthcare.Application.Waitlist;
using Healthcare.Domain.Appointments;
using Healthcare.Domain.Common;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Api.Endpoints;

public static class AvailabilityEndpoints
{
    public static void MapAvailabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/availability").WithTags("Availability").RequireAuthorization();

        g.MapPost("/search", async Task<Results<Ok<object>, ProblemHttpResult>> (
            SlotSearchRequest req, SlotEngine engine, CancellationToken ct) =>
        {
            try
            {
                var slots = await engine.SearchAsync(new SlotSearchQuery(
                    req.FacilityId, req.ClinicianId, req.AppointmentTypeId, req.FromDate, req.ToDate), ct);
                return TypedResults.Ok<object>(new { slots });
            }
            catch (InvalidOperationException ex)
            {
                return TypedResults.Problem(detail: ex.Message, statusCode: 400, title: "search.invalid");
            }
        });
    }

    public sealed record SlotSearchRequest(Guid FacilityId, Guid ClinicianId, Guid AppointmentTypeId,
        DateOnly FromDate, DateOnly ToDate);
}

public static class AppointmentEndpoints
{
    public static void MapAppointmentEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/appointments").WithTags("Appointments").RequireAuthorization();

        g.MapGet("/", async (IAppDbContext db, Guid? facilityId, Guid? clinicianId, Guid? patientId,
            CancellationToken ct) =>
        {
            IQueryable<Appointment> q = db.Appointments;
            if (facilityId is not null) q = q.Where(a => a.FacilityId == facilityId);
            if (clinicianId is not null) q = q.Where(a => a.ClinicianId == clinicianId);
            if (patientId is not null) q = q.Where(a => a.PatientId == patientId);
            var items = await q.OrderBy(a => a.StartUtc).Take(200).Select(a => new
            {
                a.Id, a.PatientId, a.ClinicianId, a.FacilityId, a.RoomId, a.AppointmentTypeId,
                a.StartUtc, a.EndUtc, status = a.Status.ToString()
            }).ToListAsync(ct);
            return TypedResults.Ok(items);
        });

        g.MapGet("/{id:guid}", async Task<Results<Ok<object>, NotFound>> (Guid id, IAppDbContext db, CancellationToken ct) =>
        {
            var a = await db.Appointments.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (a is null) return TypedResults.NotFound();
            return TypedResults.Ok<object>(new
            {
                a.Id, a.PatientId, a.ClinicianId, a.FacilityId, a.RoomId, a.AppointmentTypeId,
                a.StartUtc, a.EndUtc, status = a.Status.ToString(),
                a.ConfirmedAt, a.CheckedInAt, a.WithClinicianAt, a.AwaitingResultsAt, a.CompletedAt
            });
        });

        g.MapPost("/", async Task<Results<Created<object>, ProblemHttpResult>> (
            BookAppointmentRequest req, AppointmentBookingService svc, ICurrentUser user, CancellationToken ct) =>
        {
            try
            {
                var appt = await svc.BookAsync(
                    new BookAppointmentCommand(req.PatientId, req.ClinicianId, req.FacilityId, req.RoomId,
                        req.AppointmentTypeId, req.StartUtc),
                    user.UserId, user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.Created($"/api/v1/appointments/{appt.Id}", (object)new
                {
                    appt.Id, appt.StartUtc, appt.EndUtc, status = appt.Status.ToString()
                });
            }
            catch (DomainException ex)
            {
                var code = ex.Code == "appointment.conflict" ? 409 : 422;
                return TypedResults.Problem(detail: ex.Message, statusCode: code, title: ex.Code);
            }
            catch (DbUpdateConcurrencyException)
            {
                return TypedResults.Problem(detail: "Concurrent booking prevented duplicate.", statusCode: 409, title: "appointment.concurrency");
            }
        }).RequireAuthorization(Policies.CanBookAppointment);

        g.MapPost("/{id:guid}/reschedule", async Task<Results<NoContent, ProblemHttpResult>> (
            Guid id, RescheduleRequest req, AppointmentBookingService svc, ICurrentUser user, CancellationToken ct) =>
        {
            try
            {
                await svc.RescheduleAsync(id, req.NewStartUtc, user.UserId,
                    user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                return TypedResults.NoContent();
            }
            catch (DomainException ex)
            {
                var code = ex.Code == "appointment.conflict" ? 409 : 422;
                return TypedResults.Problem(detail: ex.Message, statusCode: code, title: ex.Code);
            }
        }).RequireAuthorization(Policies.CanBookAppointment);

        g.MapPost("/{id:guid}/cancel", async Task<Results<NoContent, ProblemHttpResult>> (
            Guid id, CancelRequest req, AppointmentBookingService svc, ICurrentUser user, WaitlistService waitlist,
            IAppDbContext db, CancellationToken ct) =>
        {
            try
            {
                var appt = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id, ct);
                await svc.CancelAsync(id, req.Reason, req.Notes, user.UserId,
                    user.Roles.FirstOrDefault() ?? "unknown", user.CorrelationId, ct);
                if (appt is not null) await waitlist.OfferSlotAsync(appt.FacilityId, appt.AppointmentTypeId, appt.Id, ct);
                return TypedResults.NoContent();
            }
            catch (DomainException ex)
            {
                return TypedResults.Problem(detail: ex.Message, statusCode: 422, title: ex.Code);
            }
        }).RequireAuthorization(Policies.CanBookAppointment);

        // Status transitions
        g.MapPost("/{id:guid}/status", async Task<Results<NoContent, ProblemHttpResult, NotFound>> (
            Guid id, StatusTransitionRequest req, IAppDbContext db, IClock clock, CancellationToken ct) =>
        {
            var appt = await db.Appointments.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (appt is null) return TypedResults.NotFound();
            try
            {
                var now = clock.UtcNow;
                switch (req.Action.ToLowerInvariant())
                {
                    case "confirm": appt.Confirm(now); break;
                    case "check-in": appt.CheckIn(now); break;
                    case "triage": appt.StartTriage(now); break;
                    case "with-clinician": appt.AdmitToClinician(now); break;
                    case "awaiting-results": appt.MoveToAwaitingResults(now); break;
                    case "complete": appt.Complete(now); break;
                    case "no-show": appt.MarkNoShow(now); break;
                    default:
                        return TypedResults.Problem(detail: $"Unknown action {req.Action}", statusCode: 400,
                            title: "appointment.action.invalid");
                }
                await db.SaveChangesAsync(ct);
                return TypedResults.NoContent();
            }
            catch (DomainException ex)
            {
                return TypedResults.Problem(detail: ex.Message, statusCode: 422, title: ex.Code);
            }
        }).RequireAuthorization(Policies.CanManageWorkflow);
    }

    public sealed record BookAppointmentRequest(
        Guid PatientId, Guid ClinicianId, Guid FacilityId, Guid RoomId, Guid AppointmentTypeId,
        DateTimeOffset StartUtc);
    public sealed record RescheduleRequest(DateTimeOffset NewStartUtc);
    public sealed record CancelRequest(CancellationReason Reason, string? Notes);
    public sealed record StatusTransitionRequest(string Action);
}
