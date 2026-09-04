using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using IntegrationHub.Application;
using IntegrationHub.Domain;
using IntegrationHub.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;

namespace IntegrationHub.Api;

public sealed record TokenRequest(string ClientId, string ClientSecret, string[]? Scopes);
public sealed record CreateFlowRequest(string Name, string Format, string Definition);
public sealed record AddFlowVersionRequest(string Format, string Definition);
public sealed record ManualRunRequest(JsonNode? Payload);
public sealed record MappingTestRequest(JsonNode? Input, IReadOnlyList<FieldMapping>? Mappings);
public sealed record ReplayRequest(Guid? ItemId, Guid? RunId, string? BatchKey);
public sealed record SetSecretRequest(string Name, string Value);

public static class Endpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/token", (
            TokenRequest request,
            TokenIssuer issuer,
            IOptions<ApiSecurityOptions> security,
            IWebHostEnvironment environment) =>
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                return Results.NotFound();
            }
            if (!CryptographicEquals(request.ClientId, security.Value.DemoClientId)
                || !CryptographicEquals(request.ClientSecret, security.Value.DemoClientSecret))
            {
                return Results.Unauthorized();
            }
            var allowed = new[] { "hub.read", "hub.write", "hub.admin" };
            var scopes = (request.Scopes is { Length: > 0 } ? request.Scopes : allowed)
                .Where(x => allowed.Contains(x, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return Results.Ok(new
            {
                access_token = issuer.Issue(request.ClientId, scopes, TimeSpan.FromHours(1)),
                token_type = "Bearer",
                expires_in = 3600,
                scope = string.Join(' ', scopes)
            });
        }).AllowAnonymous();
        return app;
    }

    public static IEndpointRouteBuilder MapConnectorEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/connectors").RequireAuthorization(Policies.Read);
        group.MapGet("/", (IConnectorRegistry registry) => Results.Ok(registry.List()));
        group.MapGet("/{id}", (string id, string? version, IConnectorRegistry registry) =>
        {
            try
            {
                return Results.Ok(registry.Get(id, version).Descriptor);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        });
        return app;
    }

    public static IEndpointRouteBuilder MapFlowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/flows");
        group.MapGet("/", async (IFlowStore store, CancellationToken token) =>
            Results.Ok(await store.ListAsync(token))).RequireAuthorization(Policies.Read);
        group.MapGet("/{id:guid}", async (Guid id, IFlowStore store, CancellationToken token) =>
            await store.GetAsync(id, token) is { } flow ? Results.Ok(flow) : Results.NotFound())
            .RequireAuthorization(Policies.Read);
        group.MapPost("/", async (
            CreateFlowRequest request,
            HttpContext context,
            IFlowStore store,
            CancellationToken token) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name)
                || request.Format is not ("json" or "yaml")
                || string.IsNullOrWhiteSpace(request.Definition))
            {
                return Validation(context, new Dictionary<string, string[]>
                {
                    ["request"] = ["Name, format (json/yaml), and definition are required."]
                });
            }
            var flow = await store.CreateAsync(
                request.Name,
                request.Format,
                request.Definition,
                context.User.Identity?.Name ?? context.User.FindFirst("sub")?.Value ?? "unknown",
                token);
            return Results.Created($"/api/v1/flows/{flow.Id}", flow);
        }).RequireAuthorization(Policies.Write);
        group.MapPost("/{id:guid}/versions", async (
            Guid id,
            AddFlowVersionRequest request,
            HttpContext context,
            IFlowStore store,
            CancellationToken token) =>
        {
            try
            {
                var version = await store.AddVersionAsync(
                    id,
                    request.Format,
                    request.Definition,
                    context.User.FindFirst("sub")?.Value ?? "unknown",
                    token);
                return Results.Created($"/api/v1/flows/{id}/versions/{version.Version}", version);
            }
            catch (KeyNotFoundException)
            {
                return Results.NotFound();
            }
        }).RequireAuthorization(Policies.Write);
        group.MapPost("/{id:guid}/versions/{version:int}/activate", async (
            Guid id,
            int version,
            IFlowStore store,
            CancellationToken token) =>
        {
            await store.ActivateAsync(id, version, token);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Write);
        group.MapPost("/{id:guid}/rollback", async (Guid id, IFlowStore store, CancellationToken token) =>
        {
            await store.RollbackAsync(id, token);
            return Results.NoContent();
        }).RequireAuthorization(Policies.Write);
        group.MapPost("/{id:guid}/runs", async (
            Guid id,
            ManualRunRequest request,
            HttpContext context,
            IFlowRunner runner,
            CancellationToken token) =>
        {
            var run = await runner.RunAsync(id, request.Payload, context.TraceIdentifier, token);
            return Results.Accepted($"/api/v1/runs/{run.Id}", run);
        }).RequireAuthorization(Policies.Write);
        return app;
    }

    public static IEndpointRouteBuilder MapRunEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/runs").RequireAuthorization(Policies.Read);
        group.MapGet("/", async (
            string? status,
            Guid? flowId,
            string? correlationId,
            int page,
            int pageSize,
            IExecutionStore store,
            CancellationToken token) =>
        {
            RunStatus? parsed = Enum.TryParse<RunStatus>(status, true, out var selected) ? selected : null;
            return Results.Ok(await store.SearchRunsAsync(
                new RunSearch(parsed, flowId, correlationId, Math.Max(page, 1), pageSize == 0 ? 50 : pageSize), token));
        });
        group.MapGet("/{id:guid}", async (Guid id, IExecutionStore store, CancellationToken token) =>
            await store.GetRunAsync(id, token) is { } run ? Results.Ok(run) : Results.NotFound());
        group.MapGet("/contract-drift", async (bool unresolvedOnly, IContractDriftStore store, CancellationToken token) =>
            Results.Ok(await store.ListAsync(unresolvedOnly, token)));
        return app;
    }

    public static IEndpointRouteBuilder MapMappingEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/mappings/test", (
            MappingTestRequest request,
            HttpContext context,
            SafeExpressionEvaluator evaluator) =>
        {
            if (request.Input is null || request.Mappings is null || request.Mappings.Count == 0)
            {
                return Validation(context, new Dictionary<string, string[]>
                {
                    ["mapping"] = ["Input and at least one mapping are required."]
                });
            }
            var result = new MappingEngine(evaluator).Map(request.Input, request.Mappings);
            return Results.Ok(result);
        }).RequireAuthorization(Policies.Write);
        return app;
    }

    public static IEndpointRouteBuilder MapDeadLetterEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/dead-letters");
        group.MapGet("/", async (string? status, IDeadLetterStore store, CancellationToken token) =>
        {
            DeadLetterStatus? parsed = Enum.TryParse<DeadLetterStatus>(status, true, out var selected) ? selected : null;
            return Results.Ok(await store.ListAsync(parsed, token));
        }).RequireAuthorization(Policies.Read);
        group.MapPost("/replay", async (
            ReplayRequest request,
            HttpContext context,
            IFlowRunner runner,
            CancellationToken token) =>
        {
            if (request.ItemId is null && request.RunId is null && string.IsNullOrWhiteSpace(request.BatchKey))
            {
                return Validation(context, new Dictionary<string, string[]>
                {
                    ["selector"] = ["Provide itemId, runId, or batchKey."]
                });
            }
            var run = await runner.ReplayAsync(
                request.ItemId, request.RunId, request.BatchKey, context.TraceIdentifier, token);
            return Results.Accepted($"/api/v1/runs/{run.Id}", run);
        }).RequireAuthorization(Policies.Write);
        return app;
    }

    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/secrets").RequireAuthorization(Policies.Admin);
        group.MapGet("/", async (ISecretStore store, CancellationToken token) =>
            Results.Ok(await store.ListAsync(token)));
        group.MapPut("/", async (SetSecretRequest request, ISecretStore store, CancellationToken token) =>
        {
            await store.SetAsync(request.Name, request.Value, token);
            return Results.NoContent();
        });
        group.MapPost("/{name}/rotate", async (
            string name,
            SetSecretRequest request,
            ISecretStore store,
            CancellationToken token) =>
        {
            var version = await store.RotateAsync(name, request.Value, token);
            return Results.Ok(new { name, version });
        });
        return app;
    }

    public static IEndpointRouteBuilder MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/webhooks/{flowId:guid}", async (
            Guid flowId,
            HttpContext context,
            IConnectorRegistry connectors,
            IFlowRunner runner,
            CancellationToken token) =>
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(token);
            var signature = context.Request.Headers["X-Hub-Signature"].ToString();
            var timestamp = context.Request.Headers["X-Hub-Timestamp"].ToString();
            var nonce = context.Request.Headers["X-Hub-Nonce"].ToString();
            try
            {
                var verified = await connectors.Get("webhook").ExecuteAsync(
                    "verify",
                    new JsonObject
                    {
                        ["body"] = body,
                        ["signature"] = signature,
                        ["timestamp"] = timestamp,
                        ["nonce"] = nonce
                    },
                    new ConnectorExecutionContext(context.TraceIdentifier),
                    token);
                var run = await runner.RunAsync(flowId, verified.Payload, context.TraceIdentifier, token);
                return Results.Accepted($"/api/v1/runs/{run.Id}", run);
            }
            catch (ConnectorException ex) when (ex.StatusCode is 401 or 422)
            {
                return Results.Problem(ex.Message, statusCode: ex.StatusCode, title: "Webhook rejected");
            }
            catch (KeyNotFoundException)
            {
                return Results.Problem("Webhook signing key is not configured.", statusCode: 503);
            }
        }).AllowAnonymous().RequireRateLimiting("webhooks");
        return app;
    }

    private static IResult Validation(HttpContext context, IDictionary<string, string[]> errors) =>
        Results.ValidationProblem(errors, extensions: new Dictionary<string, object?>
        {
            ["traceId"] = context.TraceIdentifier
        });

    private static bool CryptographicEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length
               && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
