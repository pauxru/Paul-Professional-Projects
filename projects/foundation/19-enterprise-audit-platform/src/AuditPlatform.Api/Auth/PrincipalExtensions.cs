using System.Security.Claims;
using AuditPlatform.Application.Security;
using AuditPlatform.Domain.Events;

namespace AuditPlatform.Api.Auth;

public static class PrincipalExtensions
{
    public static string GetTenantId(this ClaimsPrincipal p) => p.FindFirst("tenant")?.Value ?? string.Empty;

    public static IReadOnlyList<string> GetScopes(this ClaimsPrincipal p) => p.FindAll("scope").Select(c => c.Value).ToList();

    public static ClearanceLevel GetClearance(this ClaimsPrincipal p)
    {
        var c = p.FindFirst("clearance")?.Value ?? "standard";
        return c switch
        {
            "investigator" => ClearanceLevel.Investigator,
            "elevated" => ClearanceLevel.Elevated,
            _ => ClearanceLevel.Standard
        };
    }

    public static ReaderContext ToReader(this ClaimsPrincipal p, HttpContext ctx, bool isMetaAuditor = false)
    {
        return new ReaderContext(
            ActorId: p.FindFirst("sub")?.Value ?? "anonymous",
            ActorDisplayName: p.FindFirst("name")?.Value ?? "anonymous",
            ActorType: ActorType.User,
            Scopes: p.GetScopes(),
            TenantId: p.GetTenantId(),
            CorrelationId: ctx.Response.Headers["X-Correlation-Id"].ToString(),
            SourceIp: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            UserAgent: ctx.Request.Headers.UserAgent.ToString(),
            Clearance: p.GetClearance(),
            IsMetaAuditor: isMetaAuditor);
    }
}
