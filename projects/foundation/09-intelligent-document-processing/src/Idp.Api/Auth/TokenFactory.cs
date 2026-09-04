using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Idp.Application.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Idp.Api.Auth;

/// <summary>
/// Issues short-lived HS256 JWTs for local development and tests. This is NOT a production identity
/// provider; in production the API would validate tokens from a real issuer. The signing key is a
/// development default and must be overridden by configuration/secret outside local use.
/// </summary>
public static class TokenFactory
{
    public static string Create(
        JwtOptions options, string subject, IEnumerable<string> permissions,
        TimeSpan? lifetime = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        foreach (var perm in permissions)
            claims.Add(new Claim(Permissions.ClaimType, perm));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var now = DateTime.UtcNow;

        var token = new JwtSecurityToken(
            issuer: options.Issuer,
            audience: options.Audience,
            claims: claims,
            notBefore: now,
            expires: now.Add(lifetime ?? TimeSpan.FromHours(8)),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
