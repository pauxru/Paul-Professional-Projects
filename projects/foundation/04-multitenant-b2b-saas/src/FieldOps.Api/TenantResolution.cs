using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using FieldOps.Application;
using FieldOps.Domain;

namespace FieldOps.Api;

public sealed class TenantResolutionException(string message) : Exception(message);
public sealed class TenantAccessDeniedException(string message) : Exception(message);

public interface ITenantResolutionStrategy
{
    string Name { get; }
    ValueTask<string?> ResolveAsync(HttpContext context);
}

public sealed class JwtTenantResolutionStrategy : ITenantResolutionStrategy
{
    public string Name => "jwt";
    public ValueTask<string?> ResolveAsync(HttpContext context) =>
        ValueTask.FromResult(context.User.FindFirstValue("tenant_id"));
}

public sealed class HeaderTenantResolutionStrategy(TenantResolutionOptions options) : ITenantResolutionStrategy
{
    public string Name => "header";
    public ValueTask<string?> ResolveAsync(HttpContext context) =>
        ValueTask.FromResult(context.Request.Headers[options.HeaderName].FirstOrDefault());
}

public sealed class SubdomainTenantResolutionStrategy(TenantResolutionOptions options) : ITenantResolutionStrategy
{
    public string Name => "subdomain";

    public ValueTask<string?> ResolveAsync(HttpContext context)
    {
        var host = context.Request.Host.Host;
        var suffix = $".{options.BaseDomain}";
        if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return ValueTask.FromResult<string?>(null);
        var slug = host[..^suffix.Length];
        return ValueTask.FromResult<string?>(slug.Contains('.') ? null : slug);
    }
}

public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IEnumerable<ITenantResolutionStrategy> strategies,
        TenantResolutionOptions options,
        IOrganizationRepository organizations,
        IMembershipRepository memberships,
        IMutableTenantContext tenantContext,
        ILogger<TenantResolutionMiddleware> logger)
    {
        var path = context.Request.Path;
        var isAcceptInvitation = path.Equals("/api/v1/invitations/accept", StringComparison.OrdinalIgnoreCase);
        var tenantRequired = isAcceptInvitation
            || (path.StartsWithSegments("/api/v1")
                && !path.StartsWithSegments("/api/v1/auth/token")
                && !path.StartsWithSegments("/api/v1/billing/webhooks")
                && !path.StartsWithSegments("/api/v1/admin"));

        if (!tenantRequired || (!isAcceptInvitation && context.User.Identity?.IsAuthenticated != true))
        {
            await next(context);
            return;
        }

        var byName = strategies.ToDictionary(x => x.Name, StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string Strategy, Organization Tenant)>();
        foreach (var name in options.Precedence)
        {
            if (!byName.TryGetValue(name, out var strategy))
            {
                throw new TenantResolutionException($"Unknown tenant resolution strategy '{name}'.");
            }

            var value = await strategy.ResolveAsync(context);
            if (string.IsNullOrWhiteSpace(value)) continue;
            var tenant = Guid.TryParse(value, out var id)
                ? await organizations.FindByIdAsync(id, context.RequestAborted)
                : await organizations.FindBySlugAsync(value, context.RequestAborted);
            if (tenant is null)
            {
                throw new TenantAccessDeniedException($"Tenant supplied by {strategy.Name} is not accessible.");
            }

            candidates.Add((strategy.Name, tenant));
        }

        if (candidates.Count == 0)
        {
            throw new TenantResolutionException("No tenant could be resolved from JWT, X-Tenant, or subdomain.");
        }

        var distinct = candidates.Select(x => x.Tenant.Id).Distinct().ToArray();
        if (distinct.Length > 1)
        {
            throw new TenantResolutionException(
                $"Ambiguous tenant context from: {string.Join(", ", candidates.Select(x => x.Strategy))}.");
        }

        var resolved = candidates[0].Tenant;
        if (resolved.Status == OrganizationStatus.Suspended)
        {
            throw new TenantAccessDeniedException("The resolved tenant is suspended.");
        }

        tenantContext.Set(resolved.Id, resolved.Slug);
        if (!isAcceptInvitation && !context.User.HasClaim("platform_admin", "true"))
        {
            var userIdClaim = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? context.User.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!Guid.TryParse(userIdClaim, out var userId)
                || !await memberships.UserBelongsToTenantAsync(userId, resolved.Id, context.RequestAborted))
            {
                throw new TenantAccessDeniedException("The authenticated identity is not a member of the resolved tenant.");
            }
        }

        using (logger.BeginScope(new Dictionary<string, object>
        {
            ["TenantId"] = resolved.Id,
            ["TenantSlug"] = resolved.Slug
        }))
        {
            FieldOpsTelemetry.TagTenant(context, resolved.Id);
            await next(context);
        }
    }
}
