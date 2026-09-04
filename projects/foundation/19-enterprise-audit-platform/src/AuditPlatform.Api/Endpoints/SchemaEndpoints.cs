using AuditPlatform.Api.Auth;
using AuditPlatform.Application.Abstractions;
using AuditPlatform.Application.Schemas;

namespace AuditPlatform.Api.Endpoints;

public static class SchemaEndpoints
{
    public static IEndpointRouteBuilder MapSchemaEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/schemas").WithTags("schemas");

        g.MapGet("/", async (ISchemaRegistry registry, CancellationToken ct) =>
        {
            var all = await registry.ListAsync(ct);
            return Results.Ok(all);
        }).RequireAuthorization("audit:read");

        g.MapPost("/", async (SchemaRegistrationBody body, SchemaRegistryService svc, CancellationToken ct) =>
        {
            var result = await svc.RegisterAsync(body.EventType, body.SchemaJson, body.Description, ct);
            if (!result.Accepted)
                return Results.Problem(string.Join("; ", result.Breakages), statusCode: 409, title: "SchemaIncompatible");
            return Results.Created($"/api/v1/schemas/{body.EventType}/{result.Version}", result);
        }).RequireAuthorization("audit:admin");

        return app;
    }
}

public sealed record SchemaRegistrationBody(string EventType, string SchemaJson, string Description);
