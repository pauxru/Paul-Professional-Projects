using Microsoft.EntityFrameworkCore;

using Contoso.Payments.Infrastructure.Outbox;
using Contoso.Payments.Infrastructure.Persistence;

namespace Contoso.Payments.Api.Endpoints;

public static class AdminOutboxEndpoints
{
    public static void MapAdminOutboxEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/outbox").WithTags("Admin").RequireAuthorization("admin");

        group.MapGet("/pending", async (AppDbContext db, CancellationToken ct) =>
        {
            var items = await db.OutboxMessages.Where(m => !m.Dispatched).OrderBy(m => m.NextAttemptAtUtc)
                .Take(200).ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapGet("/dead-letters", async (AppDbContext db, CancellationToken ct) =>
        {
            var items = await db.OutboxDeadLetters.OrderByDescending(m => m.DeadLetteredAtUtc)
                .Take(200).ToListAsync(ct);
            return Results.Ok(items);
        });

        group.MapPost("/dispatch-once", async (OutboxDispatcher dispatcher, CancellationToken ct) =>
        {
            var n = await dispatcher.DispatchOnceAsync(ct);
            return Results.Ok(new { dispatched = n });
        });
    }
}
