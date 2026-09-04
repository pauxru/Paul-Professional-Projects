using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ExampleBank.Ledger.Api.Middleware;

/// <summary>
/// Translates domain and application exceptions into RFC 7807 ProblemDetails responses with stable
/// machine-readable <c>type</c>/<c>code</c> values, so financial-integrity failures are surfaced
/// clearly (e.g. an unbalanced-entry attempt returns 422, not 500).
/// </summary>
public sealed class LedgerExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;

    public LedgerExceptionHandler(IProblemDetailsService problemDetails) => _problemDetails = problemDetails;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, code) = Map(exception);
        httpContext.Response.StatusCode = status;

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception.Message,
            Type = $"https://docs.examplebank.local/errors/{code}",
        };
        problem.Extensions["code"] = code;

        if (httpContext.Items.TryGetValue("CorrelationId", out var correlationId) && correlationId is not null)
        {
            problem.Extensions["correlationId"] = correlationId;
        }

        if (exception is RequestValidationException validation)
        {
            problem.Extensions["errors"] = validation.Errors;
        }

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private static (int Status, string Title, string Code) Map(Exception exception) => exception switch
    {
        NotFoundException => (StatusCodes.Status404NotFound, "Resource not found", "not_found"),
        RequestValidationException => (StatusCodes.Status422UnprocessableEntity, "Validation failed", "validation_failed"),
        ConflictException => (StatusCodes.Status409Conflict, "Conflict", "conflict"),
        DuplicateKeyException => (StatusCodes.Status409Conflict, "Duplicate request", "duplicate_key"),
        ConcurrencyConflictException => (StatusCodes.Status409Conflict, "Concurrency conflict", "concurrency_conflict"),
        UnbalancedEntryException => (StatusCodes.Status422UnprocessableEntity, "Unbalanced journal entry", "unbalanced_entry"),
        MixedCurrencyException => (StatusCodes.Status422UnprocessableEntity, "Mixed-currency entry rejected", "mixed_currency"),
        InsufficientFundsException => (StatusCodes.Status422UnprocessableEntity, "Insufficient funds", "insufficient_funds"),
        AppendOnlyViolationException => (StatusCodes.Status409Conflict, "Append-only violation", "append_only_violation"),
        DomainException domain => (StatusCodes.Status400BadRequest, "Domain rule violation", domain.Code),
        _ => (StatusCodes.Status500InternalServerError, "Unexpected error", "internal_error"),
    };
}
