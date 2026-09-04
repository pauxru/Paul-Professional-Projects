using AgentPlatform.Application.Approvals;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AgentPlatform.Api.Observability;

/// <summary>Assigns/propagates a correlation id and sets conservative security response headers.</summary>
public sealed class RequestContextMiddleware
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";

    private readonly RequestDelegate _next;

    public RequestContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming) && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()
            : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = correlationId;

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers[HeaderName] = correlationId;
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'";
            return Task.CompletedTask;
        });

        await _next(context);
    }
}

public static class RequestContextExtensions
{
    public static string CorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(RequestContextMiddleware.ItemKey, out var value) && value is string s
            ? s
            : "unknown";
}

/// <summary>
/// Translates known exception types to RFC-7807 ProblemDetails. Model/tool failures are already
/// data (never exceptions), so this mostly covers bad requests and missing resources.
/// </summary>
public sealed class AppExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;

    public AppExceptionHandler(IProblemDetailsService problemDetails) => _problemDetails = problemDetails;

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            UnauthorizedAccessException => (StatusCodes.Status403Forbidden, "Forbidden"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            InvalidOperationException => (StatusCodes.Status400BadRequest, "Invalid request"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid argument"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error"),
        };

        context.Response.StatusCode = status;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status == StatusCodes.Status500InternalServerError ? "An unexpected error occurred." : exception.Message,
                Extensions = { ["correlationId"] = context.CorrelationId() },
            },
        });
    }
}
