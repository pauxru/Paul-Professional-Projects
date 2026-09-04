using AuditPlatform.Api.Auth;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Retention;
using AuditPlatform.Domain.Retention;
using AuditPlatform.Domain.Time;

namespace AuditPlatform.Api.Endpoints;

public static class RetentionEndpoints
{
    public static IEndpointRouteBuilder MapRetentionEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/retention").WithTags("retention");

        g.MapGet("/", async (HttpContext ctx, IRetentionStore store, CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var list = await store.ListAsync(reader.TenantId, ct);
            return Results.Ok(list);
        }).RequireAuthorization("audit:read");

        g.MapPost("/", async (
            HttpContext ctx,
            RetentionPolicyBody body,
            IRetentionStore store,
            IClock clock,
            CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var policy = RetentionPolicy.Create(Guid.NewGuid(), reader.TenantId, body.CategoryPattern, body.RetainForDays, clock.UtcNow);
            await store.AddAsync(policy, ct);
            await store.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/retention/{policy.Id}", policy);
        }).RequireAuthorization("audit:admin");

        g.MapPost("/run", async (HttpContext ctx, RetentionService svc, CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var report = await svc.RunAsync(reader.TenantId, ct);
            return Results.Ok(report);
        }).RequireAuthorization("audit:admin");

        return app;
    }
}

public sealed record RetentionPolicyBody(string CategoryPattern, int RetainForDays);
