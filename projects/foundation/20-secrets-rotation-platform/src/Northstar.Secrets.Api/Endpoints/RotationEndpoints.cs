using Northstar.Secrets.Api.Auth;
using Northstar.Secrets.Api.Middleware;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Api.Endpoints;

public static class RotationEndpoints
{
    public sealed record RequestRotationRequest(
        string SecretName,
        RotationStrategyKind Strategy,
        string IdempotencyKey,
        DateTimeOffset? MaintenanceWindowStart);

    public sealed record RollbackRequest(string Reason);

    public static IEndpointRouteBuilder MapRotationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/rotations")
            .WithTags("Rotations")
            .RequireAuthorization(ScopePolicies.OperateRotations);

        group.MapPost("/", RequestAsync);
        group.MapGet("/", ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapPost("/{id:guid}/cancel", CancelAsync);
        group.MapPost("/{id:guid}/rollback", RollbackAsync);
        group.MapPost("/{id:guid}/resume", ResumeAsync);
        return app;
    }

    private static async Task<IResult> RequestAsync(
        RequestRotationRequest request,
        HttpContext httpContext,
        PathAuthorizationService pathAuthorization,
        RotationEngine engine,
        CancellationToken cancellationToken)
    {
        var actor = httpContext.User.FindFirst("sub")?.Value ?? "unknown";
        await pathAuthorization.DemandAsync(
            actor,
            request.SecretName,
            SecretPermission.OperateRotation,
            cancellationToken);
        var rotation = await engine.RequestAsync(
            new RequestRotationCommand(
                request.SecretName,
                request.Strategy,
                request.IdempotencyKey,
                actor,
                httpContext.TraceIdentifier,
                request.MaintenanceWindowStart),
            cancellationToken);
        rotation = await engine.RunToPauseOrTerminalAsync(rotation.Id, cancellationToken);
        return Results.Accepted(
            $"/api/v1/rotations/{rotation.Id}",
            ToResponse(rotation));
    }

    private static async Task<IResult> ListAsync(
        RotationEngine engine,
        CancellationToken cancellationToken) =>
        Results.Ok((await engine.ListAsync(cancellationToken)).Select(ToResponse));

    private static async Task<IResult> GetAsync(
        Guid id,
        RotationEngine engine,
        CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await engine.GetAsync(id, cancellationToken)));

    private static async Task<IResult> CancelAsync(
        Guid id,
        RotationEngine engine,
        CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await engine.CancelAsync(id, cancellationToken)));

    private static async Task<IResult> RollbackAsync(
        Guid id,
        RollbackRequest request,
        RotationEngine engine,
        CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await engine.RollbackAsync(
            id,
            string.IsNullOrWhiteSpace(request.Reason) ? "Operator-requested rollback." : request.Reason,
            cancellationToken)));

    private static async Task<IResult> ResumeAsync(
        Guid id,
        RotationEngine engine,
        CancellationToken cancellationToken) =>
        Results.Ok(ToResponse(await engine.RunToPauseOrTerminalAsync(id, cancellationToken)));

    internal static object ToResponse(RotationOperation rotation) => new
    {
        rotation.Id,
        rotation.SecretId,
        rotation.Strategy,
        rotation.State,
        rotation.RequestedAt,
        rotation.UpdatedAt,
        rotation.AcknowledgementDeadline,
        rotation.MaintenanceWindowStart,
        rotation.PreviousVersionNumber,
        rotation.NewVersionNumber,
        rotation.FailureReason,
        acknowledgements = rotation.Acknowledgements.Select(x => new
        {
            x.ConsumerId,
            x.Status,
            x.NotificationSentAt,
            x.AcknowledgedAt
        })
    };
}
