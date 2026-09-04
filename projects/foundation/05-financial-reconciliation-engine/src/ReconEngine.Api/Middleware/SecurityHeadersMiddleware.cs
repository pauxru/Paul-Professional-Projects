namespace ReconEngine.Api.Middleware;

/// <summary>
/// Adds conservative security response headers suitable for a JSON API: disable content-type sniffing,
/// deny framing, lock down the referrer, and apply a restrictive CSP (the API serves no HTML/scripts).
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var headers = context.Response.Headers;
        headers["X-Content-Type-Options"] = "nosniff";
        headers["X-Frame-Options"] = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
        headers["X-Permitted-Cross-Domain-Policies"] = "none";
        return _next(context);
    }
}
