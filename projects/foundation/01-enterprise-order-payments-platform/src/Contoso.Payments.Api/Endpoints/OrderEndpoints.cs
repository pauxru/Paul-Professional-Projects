using System.Security.Claims;

using Microsoft.AspNetCore.Mvc;

using Contoso.Payments.Api.Middleware;
using Contoso.Payments.Application.Orders;

namespace Contoso.Payments.Api.Endpoints;

public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/orders").WithTags("Orders").RequireAuthorization("orders:write");

        group.MapPost("/", async (PlaceOrderRequest req, OrderService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var actor = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ctx.User.Identity?.Name ?? "anonymous";
            var result = await svc.PlaceOrderAsync(req, actor, ctx.CorrelationId(), ct);
            return result.IsSuccess
                ? Results.Created($"/api/v1/orders/{result.Value!.Id}", result.Value)
                : ProductEndpoints.ToProblem(ctx, result);
        });

        group.MapGet("/", async (int? page, int? pageSize, OrderService svc, CancellationToken ct) =>
        {
            var res = await svc.ListAsync(page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(res);
        });

        group.MapGet("/{id:guid}", async (Guid id, OrderService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var r = await svc.GetAsync(id, ct);
            return r.IsSuccess ? Results.Ok(r.Value) : ProductEndpoints.ToProblem(ctx, r);
        });

        group.MapPost("/{id:guid}/cancel", async (Guid id, OrderService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var actor = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
            var r = await svc.CancelAsync(id, actor, ctx.CorrelationId(), ct);
            return r.IsSuccess ? Results.Ok(r.Value) : ProductEndpoints.ToProblem(ctx, r);
        });
    }
}
