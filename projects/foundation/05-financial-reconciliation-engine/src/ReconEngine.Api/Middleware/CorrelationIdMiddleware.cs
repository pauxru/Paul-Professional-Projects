using Serilog.Context;

namespace ReconEngine.Api.Middleware;

/// <summary>
/// Ensures every request has a correlation id: it reuses an inbound <c>X-Correlation-ID</c> header or
/// mints one, echoes it back on the response, exposes it via <see cref="HttpContext.Items"/> and pushes
/// it into the Serilog <see cref="LogContext"/> so every log line for the request is correlated.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming)
                            && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Guid.NewGuid().ToString("N");

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty(ItemKey, correlationId))
        {
            await _next(context);
        }
    }
}
