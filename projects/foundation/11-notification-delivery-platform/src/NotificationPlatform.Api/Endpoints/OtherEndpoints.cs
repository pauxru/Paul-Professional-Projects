namespace NotificationPlatform.Api.Endpoints;

using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NotificationPlatform.Api.Auth;
using NotificationPlatform.Application.Analytics;
using NotificationPlatform.Application.Dlq;
using NotificationPlatform.Application.Preferences;
using NotificationPlatform.Application.Suppressions;
using NotificationPlatform.Application.Unsubscribe;
using NotificationPlatform.Domain.Common;

public static class PreferenceEndpoints
{
    public static IEndpointRouteBuilder MapPreferenceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/preferences").WithTags("Preferences").RequireAuthorization(Policies.ManagePreferences);
        group.MapGet("/{externalId}", async (string externalId, ClaimsPrincipal user, IPreferenceService svc, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            var list = await svc.ListAsync(tenantId.Value, externalId, ct);
            return Results.Ok(new { items = list });
        });
        group.MapPut("/{externalId}", async (string externalId, [FromBody] UpdatePreferenceRequest request, ClaimsPrincipal user, IPreferenceService svc, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            try
            {
                await svc.UpdateAsync(tenantId.Value, externalId, request, ct);
                return Results.NoContent();
            }
            catch (DomainException ex)
            {
                return Results.Problem(title: ex.Message, statusCode: 404);
            }
        });
        return app;
    }
}

public static class SuppressionEndpoints
{
    public static IEndpointRouteBuilder MapSuppressionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/suppressions").WithTags("Suppressions").RequireAuthorization(Policies.ManageSuppressions);
        group.MapGet("/", async ([FromQuery] int? page, [FromQuery] int? pageSize, ClaimsPrincipal user, ISuppressionService svc, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            var p = page.GetValueOrDefault(1);
            var ps = pageSize.GetValueOrDefault(25);
            if (p <= 0) p = 1;
            if (ps <= 0) ps = 25;
            var items = await svc.ListAsync(tenantId.Value, p, ps, ct);
            var total = await svc.CountAsync(tenantId.Value, ct);
            return Results.Ok(new { items, page = p, pageSize = ps, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)ps) });
        });
        group.MapPost("/", async ([FromBody] AddSuppressionRequest request, ClaimsPrincipal user, ISuppressionService svc, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            var dto = await svc.AddAsync(tenantId.Value, request, ct);
            return Results.Created($"/api/v1/suppressions/{dto.Id}", dto);
        });
        group.MapDelete("/{id:guid}", async (Guid id, ClaimsPrincipal user, ISuppressionService svc, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            var removed = await svc.RemoveAsync(tenantId.Value, id, ct);
            return removed ? Results.NoContent() : Results.NotFound();
        });
        return app;
    }
}

public static class UnsubscribeEndpoints
{
    public static IEndpointRouteBuilder MapUnsubscribeEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/unsubscribe").WithTags("Unsubscribe");
        group.MapPost("/issue", async ([FromBody] UnsubscribeIssueRequest request, ClaimsPrincipal user, IUnsubscribeService svc, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            try
            {
                var result = await svc.IssueAsync(tenantId.Value, request, ct);
                return Results.Ok(result);
            }
            catch (DomainException ex)
            {
                return Results.Problem(title: ex.Message, statusCode: 404);
            }
        }).RequireAuthorization(Policies.ManagePreferences);

        group.MapGet("/{token}", async (string token, IUnsubscribeService svc, CancellationToken ct) =>
        {
            var result = await svc.HandleAsync(token, ct);
            return result.Success
                ? Results.Ok(new { unsubscribed = true, category = result.Category })
                : Results.Problem(title: result.Reason ?? "invalid", statusCode: 400);
        }).AllowAnonymous();

        return app;
    }
}

public static class DlqEndpoints
{
    public static IEndpointRouteBuilder MapDlqEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/dlq").WithTags("DLQ").RequireAuthorization(Policies.ManageDlq);
        group.MapGet("/", async ([FromQuery] int? page, [FromQuery] int? pageSize, ClaimsPrincipal user, IDlqService svc, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            var p = page.GetValueOrDefault(1);
            var ps = pageSize.GetValueOrDefault(25);
            if (p <= 0) p = 1;
            if (ps <= 0) ps = 25;
            var items = await svc.ListAsync(tenantId.Value, p, ps, ct);
            var total = await svc.CountAsync(tenantId.Value, ct);
            return Results.Ok(new { items, page = p, pageSize = ps, totalCount = total, totalPages = (int)Math.Ceiling(total / (double)ps) });
        });
        group.MapPost("/replay", async ([FromBody] IReadOnlyList<Guid> ids, ClaimsPrincipal user, IDlqService svc, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            if (ids is null || ids.Count == 0) return Results.Problem(title: "empty_ids", statusCode: 400);
            var result = await svc.ReplayAsync(tenantId.Value, ids, ct);
            return Results.Ok(result);
        });
        return app;
    }
}

public static class AnalyticsEndpoints
{
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/analytics").WithTags("Analytics").RequireAuthorization(Policies.ViewAnalytics);
        group.MapGet("/summary", async ([FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, ClaimsPrincipal user, IAnalyticsService svc, CancellationToken ct) =>
        {
            var tenantId = user.TenantId();
            if (tenantId is null) return Results.Problem(statusCode: 403);
            var toV = to ?? DateTimeOffset.UtcNow.AddDays(1);
            var fromV = from ?? toV.AddDays(-30);
            var summary = await svc.GetSummaryAsync(tenantId.Value, fromV, toV, ct);
            return Results.Ok(summary);
        });
        group.MapGet("/providers", async (IAnalyticsService svc, CancellationToken ct) =>
        {
            var providers = await svc.GetProviderHealthAsync(ct);
            return Results.Ok(new { items = providers });
        });
        group.MapGet("/queue", async (IAnalyticsService svc, CancellationToken ct) =>
        {
            var snap = await svc.GetQueueSnapshotAsync(ct);
            return Results.Ok(snap);
        });
        return app;
    }
}
