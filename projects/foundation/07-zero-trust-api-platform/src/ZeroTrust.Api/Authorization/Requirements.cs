using System.Security.Claims;

namespace ZeroTrust.Api.Authorization;

public sealed record ScopeRequirement(string Scope) : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

public sealed record RoleRequirement(string Role) : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

public sealed record StepUpRequirement(string RequiredAmr, string RequiredAcr) : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

public sealed record ClientTypeRequirement(string RequiredType) : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

public sealed record AccountOwnerRequirement() : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement;

public static class ClaimsPrincipalExtensions
{
    public static IEnumerable<string> Scopes(this ClaimsPrincipal p) =>
        (p.FindFirst("scope")?.Value ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public static bool HasScope(this ClaimsPrincipal p, string scope) =>
        p.Scopes().Any(s => string.Equals(s, scope, StringComparison.Ordinal));

    public static string? Subject(this ClaimsPrincipal p) =>
        p.FindFirst("sub")?.Value ?? p.FindFirst(ClaimTypes.NameIdentifier)?.Value;

    public static string? Amr(this ClaimsPrincipal p) => p.FindFirst("amr")?.Value;
    public static string? Acr(this ClaimsPrincipal p) => p.FindFirst("acr")?.Value;
    public static string? PrincipalType(this ClaimsPrincipal p) => p.FindFirst("typ")?.Value;
    public static bool HasRole(this ClaimsPrincipal p, string role) =>
        p.FindAll("roles").Any(c => string.Equals(c.Value, role, StringComparison.Ordinal));
}
