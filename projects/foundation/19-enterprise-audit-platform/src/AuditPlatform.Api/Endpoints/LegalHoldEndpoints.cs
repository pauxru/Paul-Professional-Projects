using AuditPlatform.Api.Auth;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Domain.Retention;
using AuditPlatform.Domain.Time;

namespace AuditPlatform.Api.Endpoints;

public static class LegalHoldEndpoints
{
    public static IEndpointRouteBuilder MapLegalHoldEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/legal-holds").WithTags("legal-holds");

        g.MapGet("/", async (HttpContext ctx, ILegalHoldStore store, CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var list = await store.ListActiveAsync(reader.TenantId, ct);
            return Results.Ok(list);
        }).RequireAuthorization("audit:read");

        g.MapPost("/", async (HttpContext ctx, LegalHoldBody body, ILegalHoldStore store, IClock clock, CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var hold = LegalHold.Apply(Guid.NewGuid(), reader.TenantId, body.ResourceType, body.ResourceId, body.Reason, body.TicketReference, clock.UtcNow);
            await store.AddAsync(hold, ct);
            await store.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/legal-holds/{hold.Id}", hold);
        }).RequireAuthorization("audit:admin");

        g.MapPost("/{id:guid}/release", async (Guid id, ILegalHoldStore store, IClock clock, CancellationToken ct) =>
        {
            var hold = await store.GetAsync(id, ct);
            if (hold is null) return Results.NotFound();
            hold.Release(clock.UtcNow);
            await store.SaveChangesAsync(ct);
            return Results.Ok(hold);
        }).RequireAuthorization("audit:admin");

        return app;
    }
}

public sealed record LegalHoldBody(string ResourceType, string ResourceId, string Reason, string TicketReference);
