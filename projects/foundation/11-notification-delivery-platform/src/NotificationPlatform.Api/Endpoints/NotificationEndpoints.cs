namespace NotificationPlatform.Api.Endpoints;

using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Api.Middleware;
using NotificationPlatform.Application.Notifications;
using NotificationPlatform.Domain.Common;

public static class NotificationEndpoints
{
    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/notifications").WithTags("Notifications").RequireAuthorization(Policies.SendNotifications);

        group.MapPost("/", async (
            [FromBody] SendNotificationRequest request,
            HttpContext http,
            ClaimsPrincipal user,
            INotificationService service,
            CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing tenant claim");
            var correlationId = (string?)http.Items["CorrelationId"] ?? Guid.NewGuid().ToString("N");
            var outcome = await service.QueueAsync(tenantId.Value, request, correlationId, ct);
            return outcome.Kind switch
            {
                SendOutcomeKind.Accepted => Results.Created($"/api/v1/notifications/{outcome.NotificationId}", outcome),
                SendOutcomeKind.IdempotentReplay => Results.Ok(outcome),
                SendOutcomeKind.Deduplicated => Results.Ok(outcome),
                SendOutcomeKind.Suppressed => Results.Ok(outcome),
                SendOutcomeKind.Rejected => Results.Problem(title: outcome.Message ?? "rejected", statusCode: StatusCodes.Status422UnprocessableEntity),
                _ => Results.Problem(title: "unknown_outcome", statusCode: 500),
            };
        }).WithName("SendNotification");

        group.MapPost("/bulk", async (
            [FromBody] BulkSendRequest request,
            HttpContext http,
            ClaimsPrincipal user,
            INotificationService service,
            CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Missing tenant claim");
            if (request.Items is null || request.Items.Count == 0)
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "empty_bulk");
            var correlationId = (string?)http.Items["CorrelationId"] ?? Guid.NewGuid().ToString("N");
            var outcome = await service.QueueBulkAsync(tenantId.Value, request, correlationId, ct);
            return Results.Ok(outcome);
        }).WithName("SendBulk");

        group.MapGet("/{id:guid}", async (Guid id, ClaimsPrincipal user, INotificationService service, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: StatusCodes.Status403Forbidden);
            var dto = await service.GetAsync(tenantId.Value, id, ct);
            return dto is null ? Results.NotFound() : Results.Ok(dto);
        }).WithName("GetNotification");

        group.MapGet("/", async (
            [FromQuery] int? page,
            [FromQuery] int? pageSize,
            [FromQuery] NotificationStatus? status,
            ClaimsPrincipal user,
            INotificationService service,
            CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: StatusCodes.Status403Forbidden);
            var p = page.GetValueOrDefault(1);
            var ps = pageSize.GetValueOrDefault(25);
            if (p <= 0) p = 1;
            if (ps <= 0) ps = 25;
            var total = await service.CountAsync(tenantId.Value, status, ct);
            var items = await service.ListAsync(tenantId.Value, p, ps, status, ct);
            return Results.Ok(new
            {
                items,
                page = p,
                pageSize = ps,
                totalCount = total,
                totalPages = (int)Math.Ceiling(total / (double)ps),
            });
        }).WithName("ListNotifications");

        return app;
    }
}
