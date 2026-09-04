using AuditPlatform.Api.Auth;
using AuditPlatform.Application.Events;
using AuditPlatform.Application.Query;
using AuditPlatform.Application.Verification;
using AuditPlatform.Domain.Events;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace AuditPlatform.Api.Endpoints;

public static class EventEndpoints
{
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/events").WithTags("events");

        g.MapPost("/", async (
            IngestRequestBody body,
            HttpContext ctx,
            AuditIngestService ingest,
            CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var req = body.ToRequest(reader.TenantId, reader);
            var result = await ingest.IngestAsync(req, ct);
            if (!result.Accepted)
                return Results.Problem(result.Reason, statusCode: 422, title: "IngestRejected");
            return Results.Created($"/api/v1/events/{result.EventId}", result);
        })
        .RequireAuthorization("audit:write");

        g.MapPost("/batch", async (
            BatchIngestBody body,
            HttpContext ctx,
            AuditIngestService ingest,
            CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var requests = body.Events.Select(e => e.ToRequest(reader.TenantId, reader)).ToList();
            var results = await ingest.IngestBatchAsync(requests, ct);
            var accepted = results.Count(r => r.Accepted);
            return Results.Ok(new { total = results.Count, accepted, rejected = results.Count - accepted, results });
        })
        .RequireAuthorization("audit:write");

        g.MapGet("/", async (
            HttpContext ctx,
            QueryService query,
            [FromQuery] DateTimeOffset? from,
            [FromQuery] DateTimeOffset? to,
            [FromQuery] string? actorId,
            [FromQuery] string? action,
            [FromQuery] string? resourceType,
            [FromQuery] string? resourceId,
            [FromQuery] EventOutcome? outcome,
            [FromQuery] EventSeverity? severity,
            [FromQuery] EventCategory? category,
            [FromQuery] string? correlationId,
            [FromQuery] string? search,
            [FromQuery] int pageSize = 50,
            [FromQuery] string? cursor = null,
            CancellationToken ct = default) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var page = await query.QueryAsync(new EventFilter(
                reader.TenantId, from, to, actorId, action, resourceType, resourceId,
                outcome, severity, category, correlationId, search, pageSize, cursor), reader, ct);
            return Results.Ok(page);
        })
        .RequireAuthorization("audit:read");

        g.MapGet("/{id:guid}", async (
            Guid id,
            HttpContext ctx,
            QueryService query,
            CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var page = await query.QueryAsync(new EventFilter(
                reader.TenantId, null, null, null, null, null, null, null, null, null, null, null, 500, null), reader, ct);
            var item = page.Items.FirstOrDefault(i => i.Id == id);
            return item is null ? Results.NotFound() : Results.Ok(item);
        })
        .RequireAuthorization("audit:read");

        g.MapGet("/{id:guid}/proof", async (
            Guid id,
            HttpContext ctx,
            VerificationService verifier,
            CancellationToken ct) =>
        {
            var reader = ctx.User.ToReader(ctx);
            var proof = await verifier.BuildInclusionProofAsync(reader.TenantId, id, ct);
            return proof is null ? Results.NotFound() : Results.Ok(proof);
        })
        .RequireAuthorization("audit:verify");

        return app;
    }
}

public sealed record IngestRequestBody(
    string EventType,
    int SchemaVersion,
    DateTimeOffset EventTime,
    ActorInput Actor,
    string ActionVerb,
    EventCategory Category,
    ResourceInput Resource,
    EventOutcome Outcome,
    EventSeverity Severity,
    SourceInput Source,
    string CorrelationId,
    string? CausationId,
    string? TraceId,
    string? ClientEventId,
    System.Text.Json.JsonElement Data,
    System.Text.Json.JsonElement? Before,
    System.Text.Json.JsonElement? After)
{
    public IngestEventRequest ToRequest(string tenantId, Application.Security.ReaderContext reader) => new(
        tenantId, EventType, SchemaVersion, EventTime, Actor, ActionVerb, Category, Resource,
        Outcome, Severity, Source, CorrelationId, CausationId, TraceId, ClientEventId, Data, Before, After);
}

public sealed record BatchIngestBody(IReadOnlyList<IngestRequestBody> Events);
