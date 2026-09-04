using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Northstar.Iga.Application;

namespace Northstar.Iga.Api;

public sealed class JwtTokenIssuer(IOptions<JwtOptions> options, IClock clock) : ITokenIssuer
{
    private readonly JwtOptions _options = options.Value;

    public string Issue(string subject, IEnumerable<string> scopes, TimeSpan lifetime)
    {
        var now = clock.UtcNow;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim(ClaimTypes.NameIdentifier, subject),
            new Claim("scope", string.Join(' ', scopes.Distinct(StringComparer.OrdinalIgnoreCase)))
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            _options.Issuer,
            _options.Audience,
            claims,
            // notBefore comes from the same clock as expires. Passing null here lets the
            // handler decide, and what it decides is the ambient system clock -- so a
            // token's validity window ends up described half by the injected clock and
            // half by the real one. That is invisible for as long as the two agree.
            notBefore: now.UtcDateTime,
            expires: now.Add(lifetime).UtcDateTime,
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
