namespace Contoso.Payments.Api.Middleware;

public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;
    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var headers = ctx.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
        headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        await _next(ctx);
    }
}
