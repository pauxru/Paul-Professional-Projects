using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Lab.Application.Abstractions;
using Lab.Application.Contracts;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Lab.SampleApp.Auth;

public sealed class DevelopmentTokenIssuer(IOptions<JwtOptions> options, IClock clock) : ITokenIssuer
{
    public string Issue(string subject, IReadOnlyCollection<string> scopes)
    {
        var settings = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey));
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim(ClaimTypes.Name, subject),
            new Claim("scope", string.Join(' ', scopes))
        };
        var issuedAt = clock.UtcNow;
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            claims,
            notBefore: issuedAt.UtcDateTime,
            expires: issuedAt.AddSeconds(settings.ExpirySeconds).UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
