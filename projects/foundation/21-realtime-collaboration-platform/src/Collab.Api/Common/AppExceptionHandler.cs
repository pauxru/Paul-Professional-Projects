using Collab.Application.Contracts;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Collab.Api.Common;

/// <summary>
/// Translates application exceptions into RFC 7807 ProblemDetails. <see cref="AppException"/> types
/// carry their own status/title; validation failures surface a field-error dictionary. Anything else
/// becomes an opaque 500 so internal details never leak to clients.
/// </summary>
public sealed class AppExceptionHandler(IProblemDetailsService problemDetails, ILogger<AppExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, detail) = exception switch
        {
            ValidationAppException v => (v.StatusCode, v.Title, v.Message),
            AppException a => (a.StatusCode, a.Title, a.Message),
            UnauthorizedAccessException => (StatusCodes.Status401Unauthorized, "Unauthorized", "Authentication is required."),
            _ => (StatusCodes.Status500InternalServerError, "Server error", "An unexpected error occurred.")
        };

        if (status >= 500)
            logger.LogError(exception, "Unhandled exception");

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };

        if (httpContext.Items.TryGetValue(CorrelationIdMiddleware.HeaderName, out var correlationId) && correlationId is not null)
            problem.Extensions["correlationId"] = correlationId;

        if (exception is ValidationAppException validation)
            problem.Extensions["errors"] = validation.Errors;

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
    }
}
