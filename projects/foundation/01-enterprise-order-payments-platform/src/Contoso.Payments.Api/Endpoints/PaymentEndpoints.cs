using System.Security.Claims;

using Contoso.Payments.Api.Middleware;
using Contoso.Payments.Application.Payments;

namespace Contoso.Payments.Api.Endpoints;

public static class PaymentEndpoints
{
    public static void MapPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/payments").WithTags("Payments").RequireAuthorization("orders:write");

        group.MapPost("/authorize", async (AuthorizePaymentRequest req, PaymentService svc,
            HttpContext ctx, CancellationToken ct) =>
        {
            var key = ctx.Request.Headers[IdempotencyMiddleware.HeaderName].ToString();
            var actor = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
            var r = await svc.AuthorizeAsync(req, key, actor, ctx.CorrelationId(), ct);
            return r.IsSuccess ? Results.Ok(r.Value) : ProductEndpoints.ToProblem(ctx, r);
        });

        group.MapPost("/{id:guid}/capture", async (Guid id, PaymentService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var actor = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
            var r = await svc.CaptureAsync(id, actor, ctx.CorrelationId(), ct);
            return r.IsSuccess ? Results.Ok(r.Value) : ProductEndpoints.ToProblem(ctx, r);
        });

        group.MapPost("/{id:guid}/void", async (Guid id, PaymentService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var actor = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
            var r = await svc.VoidAsync(id, actor, ctx.CorrelationId(), ct);
            return r.IsSuccess ? Results.Ok(r.Value) : ProductEndpoints.ToProblem(ctx, r);
        });

        group.MapPost("/{id:guid}/retry-authorize", async (Guid id, PaymentService svc,
            HttpContext ctx, CancellationToken ct) =>
        {
            var actor = ctx.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "anonymous";
            var r = await svc.RetryAuthorizeAsync(id, actor, ctx.CorrelationId(), ct);
            return r.IsSuccess ? Results.Ok(r.Value) : ProductEndpoints.ToProblem(ctx, r);
        });

        group.MapGet("/{id:guid}", async (Guid id, PaymentService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var r = await svc.GetAsync(id, ct);
            return r.IsSuccess ? Results.Ok(r.Value) : ProductEndpoints.ToProblem(ctx, r);
        });
    }
}
