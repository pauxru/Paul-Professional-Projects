using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SubscriptionBilling.Application;
using SubscriptionBilling.Domain;
using SubscriptionBilling.Infrastructure;

namespace SubscriptionBilling.Api;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; set; } = "Sqlite";

    [Required]
    public string ConnectionString { get; set; } = "Data Source=billing.db";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const string DefaultSigningKey =
        "dev-only-not-a-real-secret-change-me-0123456789";

    [Required]
    public string Issuer { get; set; } = "subscription-billing-local";

    [Required]
    public string Audience { get; set; } = "subscription-billing-api";

    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = DefaultSigningKey;
}

public sealed class BillingOptions
{
    public const string SectionName = "Billing";

    public ClosedPeriodUsageBehavior ClosedPeriodUsageBehavior { get; set; } =
        ClosedPeriodUsageBehavior.Reject;

    public TaxRoundingLevel TaxRoundingLevel { get; set; } = TaxRoundingLevel.Line;

    [Range(typeof(decimal), "0", "1")]
    public decimal UsTaxRate { get; set; }

    [MinLength(1)]
    public int[] DunningRetryDays { get; set; } = [];

    public string[] OutboundWebhookEndpoints { get; set; } = [];

    [Required]
    [MinLength(32)]
    public string WebhookSigningSecret { get; set; } =
        "dev-only-not-a-real-secret-webhook-key-0123456789";
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    public string[] AllowedOrigins { get; set; } = [];
}

public sealed class LocalTokenIssuer(
    IOptions<JwtOptions> options,
    TimeProvider timeProvider) : ITokenIssuer
{
    public string Issue(string subject, IEnumerable<string> scopes, TimeSpan lifetime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("scope", string.Join(' ', scopes.Distinct(StringComparer.Ordinal)))
        };
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(lifetime).UtcDateTime,
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        };
        var handler = new JwtSecurityTokenHandler();
        return handler.WriteToken(handler.CreateToken(descriptor));
    }
}

public sealed class BillingExceptionHandler(
    ILogger<BillingExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            RequestValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid request"),
            ResourceNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            ConflictException => (StatusCodes.Status409Conflict, "Conflict"),
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException =>
                (StatusCodes.Status409Conflict, "Concurrency conflict"),
            DomainException => (StatusCodes.Status422UnprocessableEntity, "Domain rule rejected"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected error")
        };
        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled request exception");
        }
        else
        {
            logger.LogInformation(
                "Request rejected with status {StatusCode}: {Message}",
                status,
                exception.Message);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status >= 500 ? "An unexpected error occurred." : exception.Message,
            Instance = httpContext.Request.Path
        };
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;
        if (exception is RequestValidationException validation)
        {
            problem.Extensions["errors"] = validation.Errors;
        }

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}

public sealed class DatabaseHealthCheck(
    IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("SQLite database is reachable.")
            : HealthCheckResult.Unhealthy("SQLite database is not reachable.");
    }
}
