using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

using Microsoft.IdentityModel.Tokens;

using Contoso.Payments.Application.Common;

namespace Contoso.Payments.Api.Startup;

/// <summary>
/// Local dev-only token minter.  Never used in Production — startup refuses to boot with the
/// default signing key in Production.
/// </summary>
public sealed class DevTokenIssuer
{
    private readonly JwtOptions _options;

    public DevTokenIssuer(JwtOptions options) => _options = options;

    public string Issue(string subject, IEnumerable<string> scopes, TimeSpan lifetime)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        foreach (var s in scopes) claims.Add(new Claim("scope", s));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
