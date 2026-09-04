using System.Text.Json;
using Contoso.Storefront.Api.Hosting;

namespace Contoso.Storefront.Api.Middleware;

public sealed class ConnectionDrainingMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, RequestDrainTracker tracker)
    {
        using var lease = tracker.TryEnter();
        if (lease is null)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.ContentType = "application/problem+json";
            context.Response.Headers.RetryAfter = "5";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                new
                {
                    type = "https://httpstatuses.com/503",
                    title = "Service is draining",
                    status = 503,
                    detail = "This instance is completing in-flight requests and is no longer accepting new work.",
                    traceId = context.TraceIdentifier
                },
                cancellationToken: context.RequestAborted);
            return;
        }

        await next(context);
    }
}
