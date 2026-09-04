using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Northstar.Secrets.Application;
using Northstar.Secrets.Domain;

namespace Northstar.Secrets.Api.Middleware;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            ApplicationValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            ResourceNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            ForbiddenOperationException => (StatusCodes.Status403Forbidden, "Operation forbidden"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            DomainRuleException => (StatusCodes.Status422UnprocessableEntity, "Domain rule rejected"),
            CryptographicException => (StatusCodes.Status422UnprocessableEntity, "Secret integrity check failed"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error")
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled request failure for correlation {CorrelationId}",
                httpContext.TraceIdentifier);
        }

        var extensions = new Dictionary<string, object?>
        {
            ["traceId"] = Activity.Current?.Id ?? httpContext.TraceIdentifier,
            ["correlationId"] = httpContext.TraceIdentifier
        };
        if (exception is ApplicationValidationException validation)
        {
            extensions["errors"] = validation.Errors;
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status >= 500
                    ? "The request could not be completed."
                    : exception.Message,
                Extensions = extensions
            },
            Exception = exception
        });
    }
}

public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context, ILogger<CorrelationIdMiddleware> logger)
    {
        var correlationId =
            context.Request.Headers.TryGetValue(HeaderName, out var supplied) &&
            !string.IsNullOrWhiteSpace(supplied)
                ? supplied.ToString()
                : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object>
               {
                   ["CorrelationId"] = correlationId
               }))
        {
            await next(context);
        }
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "DENY";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; connect-src 'self'";
        if (context.Request.IsHttps)
        {
            context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
        }

        await next(context);
    }
}

public static class HttpContextAccessExtensions
{
    public static AccessContext ToAccessContext(this HttpContext context, string reason)
    {
        var actor = context.User.FindFirst("sub")?.Value
                    ?? context.User.Identity?.Name
                    ?? "unknown";
        return new AccessContext(
            actor,
            reason,
            context.TraceIdentifier,
            context.Connection.RemoteIpAddress?.ToString(),
            context.Request.Headers.UserAgent.ToString());
    }
}

public static class SecretNameCodec
{
    public static string Decode(string routeValue) =>
        Uri.UnescapeDataString(routeValue).Replace('~', '/');

    public static string Encode(string name) => name.Replace('/', '~');
}
