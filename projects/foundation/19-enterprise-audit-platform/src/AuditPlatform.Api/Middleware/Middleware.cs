namespace AuditPlatform.Api.Middleware;

/// <summary>
/// Correlation-id middleware — reads X-Correlation-Id from the incoming request, generates one
/// if absent, adds it to the response headers, and pushes it into the log scope so every log
/// line for this request carries the id.
/// </summary>
public sealed class CorrelationIdMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _log;
    public const string HeaderName = "X-Correlation-Id";

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var correlationId = ctx.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId)) correlationId = Guid.NewGuid().ToString("n");
        ctx.Response.Headers[HeaderName] = correlationId;
        using (_log.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await _next(ctx);
        }
    }
}

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;
    public Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["Strict-Transport-Security"] = "max-age=31536000";
        h["X-Content-Type-Options"] = "nosniff";
        h["X-Frame-Options"] = "DENY";
        h["Referrer-Policy"] = "no-referrer";
        h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
        return _next(ctx);
    }
}
