using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Northstar.Api.Options;
using Northstar.Application.Abstractions;

namespace Northstar.Api.Security;

public sealed class JwtTokenIssuer(IOptions<JwtOptions> options) : ITokenIssuer
{
    public string Issue(string subject, IReadOnlyCollection<string> scopes)
    {
        var settings = options.Value;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim("scope", string.Join(' ', scopes))
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
