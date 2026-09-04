using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using ReconEngine.Application.Common;
using ReconEngine.Application.Reconciliation;
using ReconEngine.Domain.Abstractions;

namespace ReconEngine.Api.Middleware;

/// <summary>
/// Translates unhandled exceptions into RFC-7807 ProblemDetails with a sensible status code and the
/// request's correlation id, so clients get a consistent, machine-readable error shape and internal
/// details are never leaked.
/// </summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext context, Exception exception, CancellationToken ct)
    {
        var (status, title) = Map(exception);

        if (status >= StatusCodes.Status500InternalServerError)
            _logger.LogError(exception, "Unhandled exception");
        else
            _logger.LogWarning("Request failed: {Message}", exception.Message);

        context.Response.StatusCode = status;

        var correlationId = context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var cid) ? cid?.ToString() : null;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = context,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = status >= StatusCodes.Status500InternalServerError ? "An unexpected error occurred." : exception.Message,
                Extensions = { ["correlationId"] = correlationId },
            },
        });
    }

    private static (int Status, string Title) Map(Exception ex) => ex switch
    {
        NotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
        ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
        InvalidStateTransitionException => (StatusCodes.Status409Conflict, "Invalid state transition"),
        BalanceAssertionException => (StatusCodes.Status422UnprocessableEntity, "Balance assertion failed"),
        DomainException => (StatusCodes.Status400BadRequest, "Invalid request"),
        ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
        _ => (StatusCodes.Status500InternalServerError, "Server error"),
    };
}
