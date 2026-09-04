using Microsoft.EntityFrameworkCore;

using Contoso.Payments.Application.Reconciliation;
using Contoso.Payments.Infrastructure.Persistence;

namespace Contoso.Payments.Api.Endpoints;

public static class ReconciliationEndpoints
{
    public static void MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/reconciliation/runs").WithTags("Reconciliation")
            .RequireAuthorization("reconciliation:run");

        group.MapPost("/", async (HttpRequest req, ReconciliationService svc, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms, ct);
            ms.Position = 0;
            var rows = SettlementFileParser.Parse(ms);
            var run = await svc.RunAsync("upload.csv", rows, ct);
            return Results.Ok(run);
        });

        group.MapGet("/", async (AppDbContext db, int? page, int? pageSize, CancellationToken ct) =>
        {
            var p = Math.Max(1, page ?? 1);
            var ps = Math.Clamp(pageSize ?? 20, 1, 100);
            var q = db.ReconciliationRuns.OrderByDescending(r => r.StartedAtUtc);
            var total = await q.CountAsync(ct);
            var items = await q.Skip((p - 1) * ps).Take(ps).ToListAsync(ct);
            return Results.Ok(new { items, page = p, pageSize = ps, totalCount = total });
        });

        group.MapGet("/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var run = await db.ReconciliationRuns.FirstOrDefaultAsync(r => r.Id == id, ct);
            if (run is null) return Results.NotFound();
            var discs = await db.ReconciliationDiscrepancies.Where(d => d.RunId == id).ToListAsync(ct);
            return Results.Ok(new { run, discrepancies = discs });
        });
    }
}
