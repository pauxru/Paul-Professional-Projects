using FieldOps.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Api;

public static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/v1/admin")
            .WithTags("Platform administration")
            .RequireAuthorization("platform-admin");

        admin.MapGet("/tenants", async (FieldOpsDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await db.Organizations.AsNoTracking().OrderBy(x => x.Name).ToListAsync(cancellationToken)));

        admin.MapGet("/jobs", async (FieldOpsDbContext db, CancellationToken cancellationToken) =>
            Results.Ok(await db.Jobs.IgnoreQueryFilters().AsNoTracking()
                .OrderBy(x => x.TenantId)
                .ThenBy(x => x.ScheduleStart)
                .Take(500)
                .ToListAsync(cancellationToken)));

        return app;
    }
}
