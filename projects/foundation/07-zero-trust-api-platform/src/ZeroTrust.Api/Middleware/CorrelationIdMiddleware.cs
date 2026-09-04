using Microsoft.AspNetCore.Http.Extensions;

namespace ZeroTrust.Api.Middleware;

public sealed class CorrelationIdMiddleware
{
    public const string HeaderName = "X-Correlation-Id";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        var incoming = ctx.Request.Headers[HeaderName].ToString();
        var id = string.IsNullOrWhiteSpace(incoming) ? Guid.NewGuid().ToString("N") : incoming;
        ctx.Items[HeaderName] = id;
        ctx.Response.Headers[HeaderName] = id;
        using var scope = _logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id });
        _logger.LogDebug("Request {Method} {Url}", ctx.Request.Method, ctx.Request.GetDisplayUrl());
        await _next(ctx);
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
            h["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
            h["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            return Task.CompletedTask;
        });
        await _next(ctx);
    }
}
