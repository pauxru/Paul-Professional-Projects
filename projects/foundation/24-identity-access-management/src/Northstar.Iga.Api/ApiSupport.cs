using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;
using Northstar.Iga.Infrastructure;

namespace Northstar.Iga.Api;

public static class ApiPolicies
{
    public const string Read = "iga.read";
    public const string Admin = "iga.admin";
    public const string Approve = "iga.approve";
}

public static class ClaimsPrincipalExtensions
{
    public static bool HasScope(this ClaimsPrincipal principal, string requiredScope) =>
        principal.FindAll("scope")
            .SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Any(x => x.Equals(requiredScope, StringComparison.OrdinalIgnoreCase));
}

public static class HttpContextExtensions
{
    public static string Actor(this HttpContext context) =>
        context.User.FindFirstValue(ClaimTypes.NameIdentifier) ??
        context.User.FindFirstValue("sub") ??
        "anonymous";

    public static string CorrelationId(this HttpContext context) =>
        context.Items.TryGetValue(CorrelationIdMiddleware.ItemKey, out var value)
            ? value?.ToString() ?? context.TraceIdentifier
            : context.TraceIdentifier;

    public static bool ActorMatches(this HttpContext context, Guid assertedActorId) =>
        Guid.TryParse(context.Actor(), out var authenticatedActorId) &&
        authenticatedActorId == assertedActorId;
}

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-Id";
    public const string ItemKey = "CorrelationId";

    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var incoming) &&
                            !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()[..Math.Min(incoming.ToString().Length, 100)]
            : Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = correlationId;
        context.TraceIdentifier = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; frame-ancestors 'none'";
            if (context.Request.IsHttps)
            {
                context.Response.Headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            }
            return Task.CompletedTask;
        });
        await next(context);
    }
}

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
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            DomainRuleException => (StatusCodes.Status422UnprocessableEntity, "Domain rule rejected the request"),
            DbUpdateException => (StatusCodes.Status409Conflict, "Persistence conflict"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error")
        };
        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled API exception");
        }
        else
        {
            logger.LogInformation(exception, "API request rejected with status {StatusCode}", status);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Detail = exception.Message,
                Instance = httpContext.Request.Path
            },
            Exception = exception
        });
    }
}

public sealed class DatabaseHealthCheck(IgaDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("SQLite governance store is reachable.")
            : HealthCheckResult.Unhealthy("SQLite governance store is not reachable.");
}

public sealed class HttpAuditContextAccessor(IHttpContextAccessor contextAccessor) : IAuditContextAccessor
{
    public string? SourceIp => contextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public string? UserAgent
    {
        get
        {
            var value = contextAccessor.HttpContext?.Request.Headers.UserAgent.ToString();
            return string.IsNullOrWhiteSpace(value) ? null : value[..Math.Min(value.Length, 500)];
        }
    }
}

public static class ValidationResults
{
    public static IResult Required(params (string Field, string? Value)[] values)
    {
        var errors = values
            .Where(x => string.IsNullOrWhiteSpace(x.Value))
            .ToDictionary(x => x.Field, x => new[] { $"{x.Field} is required." });
        return Results.ValidationProblem(errors);
    }

    public static IResult ActorMismatch() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "Approval identity mismatch",
        detail: "The actor identifier in the command must match the authenticated JWT subject.");
}
