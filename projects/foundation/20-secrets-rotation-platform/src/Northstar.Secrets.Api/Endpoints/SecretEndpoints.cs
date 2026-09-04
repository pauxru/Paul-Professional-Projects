using Microsoft.AspNetCore.RateLimiting;
using Northstar.Secrets.Api.Auth;
using Northstar.Secrets.Api.Middleware;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Api.Endpoints;

public static class SecretEndpoints
{
    public sealed record RegisterSecretRequest(
        string Name,
        SecretType Type,
        string OwnerTeam,
        string Environment,
        SecretCriticality Criticality,
        IReadOnlyList<string>? Tags,
        string? Description,
        int RotationIntervalHours = 720,
        int MaxAgeHours = 2160,
        int GracePeriodHours = 24,
        IReadOnlyList<Guid>? ConsumerIds = null);

    public static IEndpointRouteBuilder MapSecretEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/secrets").WithTags("Secrets");

        group.MapPost("/", RegisterAsync)
            .RequireAuthorization(ScopePolicies.ManageSecrets);
        group.MapGet("/", ListAsync)
            .RequireAuthorization(ScopePolicies.ManageSecrets);
        group.MapGet("/{name}", GetMetadataAsync)
            .RequireAuthorization(ScopePolicies.ManageSecrets);
        group.MapGet("/{name}/versions", GetVersionsAsync)
            .RequireAuthorization(ScopePolicies.ManageSecrets);
        group.MapGet("/{name}/value", ReadValueAsync)
            .RequireAuthorization(ScopePolicies.ReadValues)
            .RequireRateLimiting("value-reads");

        return app;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterSecretRequest request,
        HttpContext httpContext,
        PathAuthorizationService pathAuthorization,
        SecretLifecycleService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["Name is required."]
            });
        }

        await pathAuthorization.DemandAsync(
            Actor(httpContext),
            request.Name,
            SecretPermission.ManageMetadata,
            cancellationToken);
        var registered = await service.RegisterAsync(
            new RegisterSecretCommand(
                request.Name,
                request.Type,
                request.OwnerTeam,
                request.Environment,
                request.Criticality,
                request.Tags ?? [],
                request.Description ?? string.Empty,
                request.RotationIntervalHours,
                request.MaxAgeHours,
                request.GracePeriodHours,
                request.ConsumerIds),
            cancellationToken);
        return Results.Created(
            $"/api/v1/secrets/{SecretNameCodec.Encode(registered.Name)}",
            registered);
    }

    private static async Task<IResult> ListAsync(
        HttpContext httpContext,
        PathAuthorizationService pathAuthorization,
        SecretLifecycleService service,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var all = await pathAuthorization.FilterAsync(
            Actor(httpContext),
            await service.ListAsync(cancellationToken),
            SecretPermission.ManageMetadata,
            cancellationToken);
        return Results.Ok(new
        {
            items = all.Skip((page - 1) * pageSize).Take(pageSize),
            page,
            pageSize,
            totalCount = all.Count,
            totalPages = (int)Math.Ceiling(all.Count / (double)pageSize)
        });
    }

    private static async Task<IResult> GetMetadataAsync(
        string name,
        HttpContext httpContext,
        PathAuthorizationService pathAuthorization,
        SecretLifecycleService service,
        CancellationToken cancellationToken)
    {
        var decoded = SecretNameCodec.Decode(name);
        await pathAuthorization.DemandAsync(
            Actor(httpContext), decoded, SecretPermission.ManageMetadata, cancellationToken);
        return Results.Ok(await service.GetMetadataAsync(decoded, cancellationToken));
    }

    private static async Task<IResult> GetVersionsAsync(
        string name,
        HttpContext httpContext,
        PathAuthorizationService pathAuthorization,
        SecretLifecycleService service,
        CancellationToken cancellationToken)
    {
        var decoded = SecretNameCodec.Decode(name);
        await pathAuthorization.DemandAsync(
            Actor(httpContext), decoded, SecretPermission.ManageMetadata, cancellationToken);
        var versions = await service.GetVersionsAsync(decoded, cancellationToken);
        return Results.Ok(versions.Select(x => new
        {
            x.VersionNumber,
            x.State,
            x.CreatedAt,
            x.ActivatedAt,
            x.ExpiresAt,
            x.DestroyedAt,
            x.KeyVersion
        }));
    }

    private static async Task<IResult> ReadValueAsync(
        string name,
        string? reason,
        int? version,
        HttpContext httpContext,
        SecretLifecycleService service,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = ["Every secret-value read requires a reason."]
            });
        }

        var result = await service.ReadValueAsync(
            SecretNameCodec.Decode(name),
            version,
            httpContext.ToAccessContext(reason),
            cancellationToken);
        httpContext.Response.Headers.CacheControl = "no-store";
        return Results.Ok(result);
    }

    private static string Actor(HttpContext context) =>
        context.User.FindFirst("sub")?.Value ?? "unknown";
}
