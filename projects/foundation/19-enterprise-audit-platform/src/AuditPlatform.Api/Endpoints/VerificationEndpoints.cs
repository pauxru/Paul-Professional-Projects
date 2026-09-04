using AuditPlatform.Api.Auth;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Verification;

namespace AuditPlatform.Api.Endpoints;

public static class VerificationEndpoints
{
    public static IEndpointRouteBuilder MapVerificationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/verify", async (
            VerifyRequest body,
            HttpContext ctx,
            VerificationService verifier,
            CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var report = await verifier.VerifyAsync(reader.TenantId, body.FromSequence, body.ToSequence, ct);
            return Results.Ok(report);
        })
        .RequireAuthorization("audit:verify")
        .WithTags("verification");

        app.MapPost("/api/v1/checkpoints", async (
            HttpContext ctx,
            VerificationService verifier,
            CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var checkpoint = await verifier.WriteCheckpointAsync(reader.TenantId, ct);
            return Results.Ok(checkpoint);
        })
        .RequireAuthorization("audit:admin")
        .WithTags("verification");

        app.MapGet("/api/v1/checkpoints", async (
            HttpContext ctx,
            ICheckpointStore checkpoints,
            CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var list = await checkpoints.ListForTenantAsync(reader.TenantId, ct);
            return Results.Ok(list);
        })
        .RequireAuthorization("audit:read")
        .WithTags("verification");

        app.MapPost("/api/v1/verify/inclusion", (
            InclusionProof proof,
            VerificationService verifier) =>
        {
            var ok = verifier.VerifyInclusionProof(proof);
            return Results.Ok(new { valid = ok });
        })
        .RequireAuthorization("audit:verify")
        .WithTags("verification");

        return app;
    }
}

public sealed record VerifyRequest(long FromSequence, long ToSequence);
