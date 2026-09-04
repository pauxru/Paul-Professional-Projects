namespace NotificationPlatform.Api.Middleware;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;

    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers[HeaderName].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(correlationId))
            correlationId = Guid.NewGuid().ToString("N");

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        using var logScope = context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("NotificationPlatform").BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId });
        await _next(context).ConfigureAwait(false);
    }
}

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("X-Frame-Options", "DENY");
        headers.TryAdd("Referrer-Policy", "no-referrer");
        headers.TryAdd("Strict-Transport-Security", "max-age=31536000; includeSubDomains");
        headers.TryAdd("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
        await _next(context).ConfigureAwait(false);
    }
}
