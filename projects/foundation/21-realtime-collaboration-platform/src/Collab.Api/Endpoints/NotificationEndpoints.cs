using System.Security.Claims;
using Collab.Api.Auth;
using Collab.Application.Services;

namespace Collab.Api.Endpoints;

/// <summary>REST surface for the per-user notification inbox.</summary>
public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications").WithTags("Notifications").RequireAuthorization();

        group.MapGet("/", async (bool? unreadOnly, int? page, int? pageSize, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            Results.Ok(await service.ListAsync(user.GetUserId(), unreadOnly ?? false, page ?? 1, pageSize ?? 50, ct)));

        group.MapPost("/{id:guid}/read", async (Guid id, ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            Results.Ok(await service.MarkReadAsync(user.GetUserId(), id, ct)));

        group.MapPost("/read-all", async (ClaimsPrincipal user, NotificationService service, CancellationToken ct) =>
            Results.Ok(new { markedRead = await service.MarkAllReadAsync(user.GetUserId(), ct) }));

        return app;
    }
}
