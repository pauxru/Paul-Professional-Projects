using Northstar.Secrets.Api.Auth;
using Northstar.Secrets.Api.Middleware;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Api.Endpoints;

public static class BreakGlassEndpoints
{
    public sealed record ApprovalRequestDto(
        ApprovalOperation Operation,
        string Resource,
        string Reason);

    public sealed record BreakGlassReadRequest(Guid ApprovalId, string SecretName, int? Version, string Reason);
    public sealed record EmergencyRevokeRequest(Guid ApprovalId, string SecretName, string Reason);
    public sealed record DestroyVersionRequest(Guid ApprovalId, string SecretName, int Version, string Reason);
    public sealed record IncidentRotationRequest(Guid ApprovalId, string Application, string Reason);

    public static IEndpointRouteBuilder MapBreakGlassEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/break-glass").WithTags("Break glass");
        group.MapPost("/requests", RequestApprovalAsync)
            .RequireAuthorization(ScopePolicies.BreakGlass);
        group.MapPost("/requests/{id:guid}/approve", ApproveAsync)
            .RequireAuthorization(ScopePolicies.ApproveDestructive);
        group.MapPost("/read", ReadAsync)
            .RequireAuthorization(ScopePolicies.BreakGlass);
        group.MapPost("/revoke", RevokeAsync)
            .RequireAuthorization(ScopePolicies.BreakGlass);
        group.MapPost("/destroy", DestroyAsync)
            .RequireAuthorization(ScopePolicies.BreakGlass);
        group.MapPost("/incident", IncidentAsync)
            .RequireAuthorization(ScopePolicies.BreakGlass);
        return app;
    }

    private static async Task<IResult> RequestApprovalAsync(
        ApprovalRequestDto request,
        HttpContext context,
        BreakGlassService service,
        CancellationToken cancellationToken)
    {
        var actor = context.User.FindFirst("sub")?.Value ?? "unknown";
        var resource = NormalizeResource(request.Operation, request.Resource);
        var approval = await service.RequestAsync(
            request.Operation, resource, actor, request.Reason, cancellationToken);
        return Results.Accepted($"/api/v1/break-glass/requests/{approval.Id}", approval);
    }

    private static async Task<IResult> ApproveAsync(
        Guid id,
        HttpContext context,
        BreakGlassService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ApproveAsync(
            id,
            context.User.FindFirst("sub")?.Value ?? "unknown",
            cancellationToken));

    private static async Task<IResult> ReadAsync(
        BreakGlassReadRequest request,
        HttpContext context,
        PathAuthorizationService pathAuthorization,
        BreakGlassService service,
        CancellationToken cancellationToken)
    {
        await pathAuthorization.DemandAsync(
            context.User.FindFirst("sub")?.Value ?? "unknown",
            request.SecretName,
            SecretPermission.BreakGlass,
            cancellationToken);
        var result = await service.ReadAsync(
            request.ApprovalId,
            request.SecretName,
            request.Version,
            context.ToAccessContext(request.Reason),
            cancellationToken);
        context.Response.Headers.CacheControl = "no-store";
        return Results.Ok(result);
    }

    private static async Task<IResult> RevokeAsync(
        EmergencyRevokeRequest request,
        HttpContext context,
        PathAuthorizationService pathAuthorization,
        BreakGlassService service,
        CancellationToken cancellationToken)
    {
        await pathAuthorization.DemandAsync(
            context.User.FindFirst("sub")?.Value ?? "unknown",
            request.SecretName,
            SecretPermission.BreakGlass,
            cancellationToken);
        await service.EmergencyRevokeAsync(
            request.ApprovalId,
            request.SecretName,
            context.ToAccessContext(request.Reason),
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> DestroyAsync(
        DestroyVersionRequest request,
        HttpContext context,
        PathAuthorizationService pathAuthorization,
        BreakGlassService service,
        CancellationToken cancellationToken)
    {
        await pathAuthorization.DemandAsync(
            context.User.FindFirst("sub")?.Value ?? "unknown",
            request.SecretName,
            SecretPermission.BreakGlass,
            cancellationToken);
        await service.DestroyVersionAsync(
            request.ApprovalId,
            request.SecretName,
            request.Version,
            context.ToAccessContext(request.Reason),
            cancellationToken);
        return Results.NoContent();
    }

    private static async Task<IResult> IncidentAsync(
        IncidentRotationRequest request,
        HttpContext context,
        BreakGlassService service,
        CancellationToken cancellationToken)
    {
        var ids = await service.ForceRotateApplicationAsync(
            request.ApprovalId,
            request.Application,
            context.ToAccessContext(request.Reason),
            cancellationToken);
        return Results.Accepted(value: new { rotations = ids });
    }

    private static string NormalizeResource(ApprovalOperation operation, string resource) =>
        operation switch
        {
            ApprovalOperation.BreakGlassRead or ApprovalOperation.EmergencyRevoke =>
                SecretRecord.NormalizeName(resource),
            ApprovalOperation.IncidentRotation => resource.Trim().ToLowerInvariant(),
            ApprovalOperation.DestroyVersion => resource.Trim().ToLowerInvariant(),
            _ => resource.Trim()
        };
}
