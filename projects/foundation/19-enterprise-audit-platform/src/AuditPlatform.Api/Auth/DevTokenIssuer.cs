using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using AuditPlatform.Api.Options;
using Microsoft.IdentityModel.Tokens;

namespace AuditPlatform.Api.Auth;

/// <summary>
/// Dev-only token minter for exercising the API without an OIDC provider. Emits HS256 tokens
/// containing the caller-selected scopes, tenant, and clearance level. Not for production —
/// the Program guards against the default key running under Production.
/// </summary>
public static class DevTokenIssuer
{
    public static string Issue(JwtOptions jwt, string subject, string tenantId, IEnumerable<string> scopes, string clearance)
    {
        var handler = new JwtSecurityTokenHandler();
        var claims = new List<Claim>
        {
            new Claim("sub", subject),
            new Claim("name", subject),
            new Claim("tenant", tenantId),
            new Claim("clearance", clearance),
        };
        foreach (var s in scopes) claims.Add(new Claim("scope", s));

        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: jwt.Issuer,
            audience: jwt.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddHours(2),
            signingCredentials: creds);
        return handler.WriteToken(token);
    }
}
