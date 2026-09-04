using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using FieldOps.Application;
using FieldOps.Domain;
using FieldOps.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FieldOps.Api;

public sealed record DevTokenRequest(string Email, string TenantSlug);
public sealed record SwitchTenantRequest(string TenantSlug);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication");

        group.MapPost("/token", async (
            DevTokenRequest request,
            FieldOpsDbContext db,
            ITokenIssuer issuer,
            IWebHostEnvironment environment,
            CancellationToken cancellationToken) =>
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                return Results.NotFound();
            }

            var user = await db.Users.SingleOrDefaultAsync(x => x.Email == request.Email.ToLower(), cancellationToken);
            var tenant = await db.Organizations.SingleOrDefaultAsync(x => x.Slug == request.TenantSlug.ToLower(), cancellationToken);
            if (user is null || tenant is null) return Results.NotFound();
            var membership = await db.Memberships.IgnoreQueryFilters().SingleOrDefaultAsync(
                x => x.UserId == user.Id && x.TenantId == tenant.Id && x.IsActive,
                cancellationToken);
            if (membership is null) return Results.Forbid();
            var platformAdmin = user.Email == DemoData.PlatformAdminEmail;
            var token = issuer.Issue(user.Id, user.Email, tenant.Id, membership.Role, platformAdmin);
            return Results.Ok(new
            {
                accessToken = token,
                tokenType = "Bearer",
                expiresIn = 7200,
                tenant = new { tenant.Id, tenant.Slug, tenant.Name },
                role = membership.Role.ToString(),
                permissions = RolePermissions.For(membership.Role)
            });
        }).AllowAnonymous();

        group.MapPost("/switch", async (
            SwitchTenantRequest request,
            ClaimsPrincipal principal,
            FieldOpsDbContext db,
            ITokenIssuer issuer,
            CancellationToken cancellationToken) =>
        {
            var userIdClaim = principal.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? principal.FindFirstValue(JwtRegisteredClaimNames.Sub);
            if (!Guid.TryParse(userIdClaim, out var userId))
            {
                return Results.Unauthorized();
            }

            var user = await db.Users.SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
            var tenant = await db.Organizations.SingleOrDefaultAsync(x => x.Slug == request.TenantSlug.ToLower(), cancellationToken);
            if (user is null || tenant is null) return Results.NotFound();
            var membership = await db.Memberships.IgnoreQueryFilters().SingleOrDefaultAsync(
                x => x.UserId == userId && x.TenantId == tenant.Id && x.IsActive,
                cancellationToken);
            if (membership is null) return Results.Forbid();
            var token = issuer.Issue(user.Id, user.Email, tenant.Id, membership.Role, user.Email == DemoData.PlatformAdminEmail);
            return Results.Ok(new { accessToken = token, tenant = tenant.Slug, role = membership.Role.ToString() });
        }).RequireAuthorization();

        return app;
    }
}
