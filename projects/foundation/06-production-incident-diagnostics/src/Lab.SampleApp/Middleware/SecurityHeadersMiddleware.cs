namespace Lab.SampleApp.Middleware;

public static class SecurityHeadersMiddleware
{
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                headers["Permissions-Policy"] = "geolocation=(), microphone=(), camera=()";
                return Task.CompletedTask;
            });
            await next(context);
        });
}
