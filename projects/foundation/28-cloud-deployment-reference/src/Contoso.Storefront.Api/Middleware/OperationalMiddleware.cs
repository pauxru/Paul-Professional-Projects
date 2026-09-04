using System.Diagnostics;
using Contoso.Storefront.Api.Observability;
using Contoso.Storefront.Application.Ports;

namespace Contoso.Storefront.Api.Middleware;

public sealed class CorrelationIdMiddleware(
    RequestDelegate next,
    ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, ICorrelationContext correlationContext)
    {
        var incoming = context.Request.Headers[HeaderName].FirstOrDefault();
        var correlationId = IsValid(incoming)
            ? incoming!
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");

        var previous = correlationContext.CorrelationId;
        correlationContext.CorrelationId = correlationId;
        context.TraceIdentifier = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });
        Activity.Current?.SetTag("correlation.id", correlationId);

        using (logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId
               }))
        {
            try
            {
                await next(context);
            }
            finally
            {
                correlationContext.CorrelationId = previous;
            }
        }
    }

    private static bool IsValid(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 128 &&
        value.All(character => character is >= '!' and <= '~');
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            if (context.Request.IsHttps)
            {
                context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }

            return Task.CompletedTask;
        });

        await next(context);
    }
}

public sealed class RequestMetricsMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, StorefrontMetrics metrics)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            await next(context);
        }
        finally
        {
            metrics.RecordRequest(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                context.Response.StatusCode);
        }
    }
}
