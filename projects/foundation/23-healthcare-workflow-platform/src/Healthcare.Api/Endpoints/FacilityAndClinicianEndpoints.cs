using Healthcare.Application.Abstractions;
using Healthcare.Domain.Common;
using Healthcare.Domain.Facilities;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace Healthcare.Api.Endpoints;

public static class FacilityEndpoints
{
    public static void MapFacilityEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/facilities").WithTags("Facilities").RequireAuthorization();

        g.MapGet("/", async (IAppDbContext db, CancellationToken ct) =>
        {
            var items = await db.Facilities.Select(f => new
            {
                f.Id,
                f.Code,
                f.Name,
                f.TimeZoneId
            }).ToListAsync(ct);
            return TypedResults.Ok(items);
        });

        g.MapGet("/{id:guid}", async Task<Results<Ok<object>, NotFound>> (Guid id, IAppDbContext db, CancellationToken ct) =>
        {
            var f = await db.Facilities.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (f is null) return TypedResults.NotFound();
            var rooms = await db.Rooms.Where(r => r.FacilityId == id).ToListAsync(ct);
            var hours = await db.OperatingHours.Where(h => h.FacilityId == id).OrderBy(h => h.Day).ToListAsync(ct);
            return TypedResults.Ok<object>(new
            {
                f.Id, f.Code, f.Name, f.TimeZoneId,
                rooms = rooms.Select(r => new { r.Id, r.Name, capabilities = r.Capabilities }),
                hours = hours.Select(h => new { day = h.Day.ToString(), open = h.Open.ToString("HH:mm"), close = h.Close.ToString("HH:mm") })
            });
        });

        g.MapPost("/", async Task<Results<Created<object>, ProblemHttpResult>> (
            CreateFacilityRequest req, IAppDbContext db, CancellationToken ct) =>
        {
            try
            {
                var f = Facility.Create(req.Code, req.Name, req.TimeZoneId);
                db.Facilities.Add(f);
                await db.SaveChangesAsync(ct);
                return TypedResults.Created($"/api/v1/facilities/{f.Id}", (object)new { f.Id, f.Code, f.Name });
            }
            catch (Domain.Common.DomainException ex)
            {
                return TypedResults.Problem(detail: ex.Message, statusCode: 422, title: ex.Code);
            }
        }).RequireAuthorization(Policies.CanManageFacility);
    }

    public sealed record CreateFacilityRequest(string Code, string Name, string TimeZoneId);
}

public static class ClinicianEndpoints
{
    public static void MapClinicianEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/clinicians").WithTags("Clinicians").RequireAuthorization();

        g.MapGet("/", async (IAppDbContext db, CancellationToken ct) =>
        {
            var items = await db.Clinicians.Select(c => new
            {
                c.Id, c.GivenName, c.FamilyName, c.Speciality,
                c.MaxPatientsPerDay, c.MinBreakBetweenMinutes
            }).ToListAsync(ct);
            return TypedResults.Ok(items);
        });

        g.MapGet("/{id:guid}", async Task<Results<Ok<object>, NotFound>> (Guid id, IAppDbContext db, CancellationToken ct) =>
        {
            var c = await db.Clinicians.FirstOrDefaultAsync(x => x.Id == id, ct);
            if (c is null) return TypedResults.NotFound();
            var patterns = await db.WorkingPatterns.Where(p => p.ClinicianId == id).ToListAsync(ct);
            var leaves = await db.LeavePeriods.Where(l => l.ClinicianId == id).ToListAsync(ct);
            return TypedResults.Ok<object>(new
            {
                c.Id, c.GivenName, c.FamilyName, c.Speciality,
                c.MaxPatientsPerDay, c.MinBreakBetweenMinutes,
                patterns = patterns.Select(p => new { day = p.Day.ToString(), start = p.Start.ToString("HH:mm"), end = p.End.ToString("HH:mm") }),
                leaves = leaves.Select(l => new { l.StartDate, l.EndDate, l.Reason })
            });
        });
    }
}
