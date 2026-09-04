using System.Security.Claims;

using Contoso.Payments.Api.Middleware;
using Contoso.Payments.Application.Refunds;

namespace Contoso.Payments.Api.Endpoints;

public static class RefundEndpoints
{
    public static void MapRefundEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/refunds").WithTags("Refunds").RequireAuthorization("orders:write");

        group.MapPost("/", async (IssueRefundRequest req, RefundService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var actor = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
            var r = await svc.IssueAsync(req, actor, ctx.CorrelationId(), ct);
            return r.IsSuccess
                ? Results.Created($"/api/v1/refunds/{r.Value!.Id}", r.Value)
                : ProductEndpoints.ToProblem(ctx, r);
        });

        group.MapGet("/", async (int? page, int? pageSize, RefundService svc, CancellationToken ct) =>
        {
            var res = await svc.ListAsync(page ?? 1, pageSize ?? 20, ct);
            return Results.Ok(res);
        });
    }
}
