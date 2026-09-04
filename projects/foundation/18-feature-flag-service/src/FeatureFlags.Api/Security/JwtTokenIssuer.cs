using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using FeatureFlags.Api.Contracts;
using Microsoft.IdentityModel.Tokens;

namespace FeatureFlags.Api.Security;

public interface ITokenIssuer
{
    string Issue(string actor, IEnumerable<string> scopes);
}

public sealed class JwtTokenIssuer(JwtOptions options) : ITokenIssuer
{
    public string Issue(string actor, IEnumerable<string> scopes)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, actor),
            new Claim(ClaimTypes.NameIdentifier, actor),
            new Claim(ClaimTypes.Name, actor),
            new Claim("scope", string.Join(' ', scopes))
        };
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims, notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddHours(4), signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class ClaimsPrincipalExtensions
{
    public static string Actor(this ClaimsPrincipal principal) => principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.Identity?.Name ?? "unknown";

    public static bool HasScope(this ClaimsPrincipal principal, string scope) => principal.Claims
        .Where(claim => claim.Type is "scope" or "scp")
        .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Contains(scope, StringComparer.Ordinal);
}
