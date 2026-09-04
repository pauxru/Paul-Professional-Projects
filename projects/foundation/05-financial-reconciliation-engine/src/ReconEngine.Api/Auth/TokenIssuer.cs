using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using ReconEngine.Domain.Abstractions;

namespace ReconEngine.Api.Auth;

/// <summary>
/// Issues signed JWTs for the demo/test flow. In production, tokens would come from a real identity
/// provider; this issuer exists so the API can be exercised end-to-end (and so 401/403 behaviour can be
/// tested) without standing up external infrastructure. Each requested scope becomes a <c>scope</c> claim.
/// </summary>
public sealed class TokenIssuer
{
    private readonly JwtSettings _settings;
    private readonly IClock _clock;

    public TokenIssuer(IOptions<JwtSettings> settings, IClock clock)
    {
        _settings = settings.Value;
        _clock = clock;
    }

    public (string Token, DateTime ExpiresAtUtc) Issue(string subject, IEnumerable<string> scopes)
    {
        var now = _clock.UtcNow;
        var expires = now.AddMinutes(_settings.AccessTokenMinutes);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, string.IsNullOrWhiteSpace(subject) ? "demo-user" : subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
            claims.Add(new Claim("scope", scope));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.Key));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(
            issuer: _settings.Issuer,
            audience: _settings.Audience,
            claims: claims,
            notBefore: now,
            expires: expires,
            signingCredentials: creds);

        return (new JwtSecurityTokenHandler().WriteToken(jwt), expires);
    }
}
