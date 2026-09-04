using System.Text;
using AuditPlatform.Api.Auth;
using AuditPlatform.Application.Exports;
using AuditPlatform.Application.Query;

namespace AuditPlatform.Api.Endpoints;

public static class ExportEndpoints
{
    public static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/exports").WithTags("exports");

        g.MapGet("/ndjson", async (HttpContext ctx, ExportService svc, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var filter = new EventFilter(reader.TenantId, from, to, null, null, null, null, null, null, null, null, null, 500, null);
            ctx.Response.ContentType = "application/x-ndjson";
            await foreach (var line in svc.ExportNdjsonAsync(filter, ct))
            {
                await ctx.Response.WriteAsync(line + "\n", ct);
            }
        }).RequireAuthorization("audit:export");

        g.MapGet("/csv", async (HttpContext ctx, ExportService svc, DateTimeOffset? from, DateTimeOffset? to, CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var filter = new EventFilter(reader.TenantId, from, to, null, null, null, null, null, null, null, null, null, 500, null);
            ctx.Response.ContentType = "text/csv";
            await foreach (var line in svc.ExportCsvAsync(filter, ct))
            {
                await ctx.Response.WriteAsync(line + "\n", ct);
            }
        }).RequireAuthorization("audit:export");

        g.MapPost("/evidence", async (HttpContext ctx, ExportService svc, EvidenceRequest body, CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var pack = await svc.BuildEvidencePackAsync(reader.TenantId, body.From, body.To, ct);
            return Results.Ok(pack);
        }).RequireAuthorization("audit:export");

        g.MapPost("/evidence/verify", (EvidencePack pack, ExportService svc) =>
        {
            var ok = svc.VerifyEvidencePack(pack);
            return Results.Ok(new { valid = ok });
        }).RequireAuthorization("audit:verify");

        return app;
    }
}

public sealed record EvidenceRequest(DateTimeOffset From, DateTimeOffset To);
