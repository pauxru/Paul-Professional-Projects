using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FieldOps.Application;
using FieldOps.Domain;
using FieldOps.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;

namespace FieldOps.Api;

public static class FieldOpsTelemetry
{
    public const string SourceName = "FieldOps";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> Requests = Meter.CreateCounter<long>("fieldops.requests");
    private static readonly Counter<long> SecurityDenials = Meter.CreateCounter<long>("fieldops.security.denials");

    public static void TagTenant(HttpContext context, Guid tenantId)
    {
        context.TraceIdentifier = context.TraceIdentifier;
        Activity.Current?.SetTag("saas.tenant_id", tenantId);
        Requests.Add(1, new KeyValuePair<string, object?>("tenant.id", tenantId));
    }

    public static void RecordSecurityDenial(Guid? tenantId, string reason) =>
        SecurityDenials.Add(
            1,
            new KeyValuePair<string, object?>("tenant.id", tenantId?.ToString("N") ?? "unresolved"),
            new KeyValuePair<string, object?>("reason", reason));
}

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var (status, type, title) = exception switch
        {
            TenantResolutionException => (400, "https://fieldops.local/problems/tenant-resolution", "Tenant resolution failed"),
            TenantAccessDeniedException => (403, "https://fieldops.local/problems/tenant-access", "Tenant access denied"),
            CrossTenantAccessException => (403, "https://fieldops.local/problems/cross-tenant-access", "Cross-tenant access denied"),
            ForbiddenOperationException => (403, "https://fieldops.local/problems/forbidden", "Operation forbidden"),
            EntitlementDeniedException => (402, "https://fieldops.local/problems/upgrade-required", "Plan upgrade required"),
            QuotaExceededException quota when quota.RateLimit => (429, "https://fieldops.local/problems/rate-limit", "Usage rate limit exceeded"),
            QuotaExceededException => (402, "https://fieldops.local/problems/quota-upgrade", "Usage quota exceeded"),
            KeyNotFoundException => (404, "https://fieldops.local/problems/not-found", "Resource not found"),
            DomainRuleException => (422, "https://fieldops.local/problems/domain-rule", "Business rule rejected"),
            BadHttpRequestException => (400, "https://fieldops.local/problems/validation", "Request validation failed"),
            _ => (500, "https://fieldops.local/problems/internal", "Unexpected server error")
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled request failure for {Path}", httpContext.Request.Path);
        }
        else
        {
            logger.LogWarning(exception, "Request rejected for {Path}", httpContext.Request.Path);
        }

        if (status is 403 or 400)
        {
            FieldOpsTelemetry.RecordSecurityDenial(
                httpContext.RequestServices.GetService<ITenantContext>()?.TenantId,
                exception.GetType().Name);
        }

        httpContext.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Type = type,
                Title = title,
                Detail = status == 500 ? "The request could not be completed." : exception.Message,
                Instance = httpContext.Request.Path
            }
        });
    }
}

public sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

public sealed class PermissionAuthorizationHandler(IPermissionService permissions)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        PermissionRequirement requirement)
    {
        var userIdText = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? context.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
        if (Guid.TryParse(userIdText, out var userId)
            && await permissions.HasPermissionAsync(userId, requirement.Permission, CancellationToken.None))
        {
            context.Succeed(requirement);
        }
    }
}

public interface ITokenIssuer
{
    string Issue(Guid userId, string email, Guid tenantId, MemberRole role, bool platformAdmin);
}

public sealed class JwtTokenIssuer(JwtOptions options, IClock clock) : ITokenIssuer
{
    public string Issue(Guid userId, string email, Guid tenantId, MemberRole role, bool platformAdmin)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(JwtRegisteredClaimNames.Email, email),
            new("tenant_id", tenantId.ToString()),
            new("role", role.ToString())
        };
        claims.AddRange(RolePermissions.For(role).Select(x => new Claim("permission", x)));
        if (platformAdmin) claims.Add(new Claim("platform_admin", "true"));

        // Every time field comes from the injected clock, including the two the library
        // would otherwise fill in for us.
        //
        // Setting only Expires and letting SecurityTokenDescriptor default NotBefore and
        // IssuedAt to DateTime.UtcNow means a token whose lifetime is described half by
        // the injected clock and half by the ambient one. That is invisible while the two
        // agree, and it is not a subtle failure when they stop: the handler enforces
        // Expires > NotBefore and throws IDX12401, so the token endpoint returns 500
        // rather than issuing a token that is merely wrong.
        //
        // Any test that freezes the clock at a fixed instant is therefore a time bomb
        // primed to go off on the day the real calendar walks past that instant. This one
        // did exactly that.
        var issuedAt = clock.UtcNow.UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Issuer = options.Issuer,
            Audience = options.Audience,
            IssuedAt = issuedAt,
            NotBefore = issuedAt,
            Expires = issuedAt.AddMinutes(options.TokenMinutes),
            SigningCredentials = new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
                SecurityAlgorithms.HmacSha256)
        };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityTokenHandler().CreateToken(descriptor));
    }
}

public static class SecurityRegistration
{
    public static IServiceCollection AddFieldOpsSecurity(this IServiceCollection services, JwtOptions jwt)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwt.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwt.Audience,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = ClaimTypes.NameIdentifier
                };
            });

        // The issuer stamps tokens from IClock, so the validator has to read the same
        // clock or the two halves of the auth path disagree about what time it is. The
        // default lifetime validator reads DateTime.UtcNow directly, which is correct in
        // production -- where IClock *is* the system clock -- and wrong everywhere the
        // clock is controlled: every test, and every replay of a recorded scenario.
        //
        // This has to be a post-configure rather than part of the block above, because
        // TokenValidationParameters is constructed before the service provider exists and
        // the alternative is a static clock accessor, which two WebApplicationFactory
        // instances in one process would fight over.
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IClock>((options, clock) =>
            {
                options.TokenValidationParameters.LifetimeValidator =
                    (notBefore, expires, _, parameters) =>
                    {
                        var now = clock.UtcNow.UtcDateTime;
                        var skew = parameters.ClockSkew;
                        if (notBefore is not null && now.Add(skew) < notBefore.Value) return false;
                        if (expires is not null && now.Subtract(skew) > expires.Value) return false;
                        return true;
                    };
            });

        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddAuthorization(options =>
        {
            foreach (var permission in Permissions.All)
            {
                options.AddPolicy(permission, policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new PermissionRequirement(permission));
                });
            }

            options.AddPolicy("platform-admin", policy =>
                policy.RequireAuthenticatedUser().RequireClaim("platform_admin", "true"));
        });
        services.AddSingleton(jwt);
        services.AddScoped<ITokenIssuer, JwtTokenIssuer>();
        return services;
    }
}

public sealed class DatabaseHealthCheck(FieldOpsDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("SQLite is reachable.")
                : HealthCheckResult.Unhealthy("SQLite is unreachable.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("SQLite health check failed.", exception);
        }
    }
}
