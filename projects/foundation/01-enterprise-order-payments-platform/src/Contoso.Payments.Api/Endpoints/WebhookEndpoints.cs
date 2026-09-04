using System.IO;

using Contoso.Payments.Api.Middleware;
using Contoso.Payments.Application.Webhooks;
using Contoso.Payments.Infrastructure.Observability;

namespace Contoso.Payments.Api.Endpoints;

public static class WebhookEndpoints
{
    public const string SignatureHeader = "X-Contoso-Signature";

    public static void MapWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/webhooks").WithTags("Webhooks");

        group.MapPost("/payments", async (WebhookService svc, HttpContext ctx, PaymentMetrics metrics, CancellationToken ct) =>
        {
            using var ms = new MemoryStream();
            await ctx.Request.Body.CopyToAsync(ms, ct);
            var body = ms.ToArray();
            var signature = ctx.Request.Headers[SignatureHeader].ToString();
            var result = await svc.ProcessAsync(signature, body, ctx.CorrelationId(), ct);
            if (result.Success) metrics.WebhooksAccepted.Add(1);
            else metrics.WebhooksRejected.Add(1);
            ctx.Response.StatusCode = result.HttpStatus;
            return Results.Text(System.Text.Json.JsonSerializer.Serialize(new { detail = result.Detail, replay = result.Replay }),
                "application/json", statusCode: result.HttpStatus);
        }).AllowAnonymous(); // Signature IS the auth.
    }
}
