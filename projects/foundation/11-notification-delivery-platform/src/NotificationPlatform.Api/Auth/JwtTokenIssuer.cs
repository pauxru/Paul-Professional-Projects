namespace NotificationPlatform.Api.Auth;

using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using NotificationPlatform.Application.Options;
using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Abstractions;

public interface ITokenIssuer
{
    string Issue(Guid tenantId, IEnumerable<string> scopes, TimeSpan lifetime);
}

public sealed class JwtTokenIssuer : ITokenIssuer
{
    private readonly JwtOptions _options;
    private readonly IClock _clock;

    public JwtTokenIssuer(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    public string Issue(Guid tenantId, IEnumerable<string> scopes, TimeSpan lifetime)
    {
        var handler = new JwtSecurityTokenHandler();
        var claims = new List<Claim>
        {
            new(ClaimNames.TenantId, tenantId.ToString()),
            new(ClaimNames.Scope, string.Join(' ', scopes)),
            new(JwtRegisteredClaimNames.Sub, tenantId.ToString()),
        };
        foreach (var scope in scopes)
        {
            claims.Add(new Claim("scp", scope));
        }
        var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var creds = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);
        var now = _clock.UtcNow.UtcDateTime;
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.AddSeconds(-5),
            expires: now + lifetime,
            signingCredentials: creds);
        return handler.WriteToken(token);
    }
}
