using System.Security.Claims;

namespace Collab.Api.Auth;

/// <summary>Reads the collaboration identity out of a validated principal (REST or hub).</summary>
public static class ClaimsPrincipalExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(subject, out var id)
            ? id
            : throw new UnauthorizedAccessException("The token is missing a valid subject claim.");
    }

    public static bool TryGetUserId(this ClaimsPrincipal principal, out Guid userId)
    {
        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(subject, out userId);
    }

    public static string GetUserName(this ClaimsPrincipal principal) =>
        principal.FindFirst("name")?.Value
        ?? principal.FindFirst(ClaimTypes.Name)?.Value
        ?? principal.FindFirst("email")?.Value
        ?? "Unknown";
}
