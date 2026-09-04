using Microsoft.AspNetCore.Diagnostics;
using Northstar.Application.Claims;
using Northstar.Domain.Common;

namespace Northstar.Api.Security;

public sealed class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            DomainRuleException => (StatusCodes.Status422UnprocessableEntity, "Domain rule rejected"),
            ArgumentException => (StatusCodes.Status422UnprocessableEntity, "Request rejected"),
            ResourceNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            ConcurrencyConflictException => (StatusCodes.Status409Conflict, "Concurrency conflict"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
        };

        logger.LogError(exception, "Request failed with status {StatusCode}", status);
        httpContext.Response.StatusCode = status;
        await Results.Problem(
            statusCode: status,
            title: title,
            detail: exception.Message,
            extensions: new Dictionary<string, object?> { ["traceId"] = httpContext.TraceIdentifier })
            .ExecuteAsync(httpContext);
        return true;
    }
}
