using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace JobScheduler.Api.Auth;

/// <summary>Issues signed HS256 access tokens carrying the requested scopes. Dev/demo utility.</summary>
public sealed class TokenIssuer(IOptions<JwtOptions> options, JobScheduler.Application.Abstractions.IClock clock)
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTimeOffset ExpiresAt) Issue(string subject, IEnumerable<string> scopes)
    {
        var now = clock.UtcNow;
        var expires = now.AddMinutes(_options.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
            new("name", subject)
        };
        foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
        {
            claims.Add(new Claim(AuthConstants.ScopeClaim, scope));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
