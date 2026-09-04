using Microsoft.Extensions.Primitives;

namespace Healthcare.Api.Middleware;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;
    public CorrelationIdMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var incoming = ctx.Request.Headers[HeaderName].FirstOrDefault();
        var corr = string.IsNullOrWhiteSpace(incoming) ? Guid.NewGuid().ToString("N") : incoming;
        ctx.Items[HeaderName] = corr;
        ctx.Response.Headers[HeaderName] = corr;
        using (var _ = ctx.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger("CorrelationId").BeginScope(new Dictionary<string, object> { ["correlationId"] = corr }))
        {
            await _next(ctx);
        }
    }
}

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;
    public async Task InvokeAsync(HttpContext ctx)
    {
        ctx.Response.OnStarting(() =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
            if (!h.ContainsKey("Content-Security-Policy"))
                h["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; img-src 'self' data:";
            return Task.CompletedTask;
        });
        await _next(ctx);
    }
}
