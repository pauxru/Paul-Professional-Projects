using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using EnterpriseSearch.Application.Search;
using EnterpriseSearch.Application.Security;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace EnterpriseSearch.Infrastructure.Security;

public sealed class JwtTokenIssuer(IOptions<JwtOptions> options, IClock clock) : ITokenIssuer
{
    public string Issue(TokenIssueRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Subject);
        var configuration = options.Value;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, request.Subject),
            new(ClaimTypes.NameIdentifier, request.Subject)
        };
        claims.AddRange(request.Scopes.Distinct(StringComparer.OrdinalIgnoreCase).Select(scope => new Claim("scope", scope)));
        claims.AddRange(request.Groups.Distinct(StringComparer.OrdinalIgnoreCase).Select(group => new Claim("groups", group)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(configuration.Issuer, configuration.Audience, claims, clock.UtcNow.UtcDateTime, clock.UtcNow.AddMinutes(configuration.LifetimeMinutes).UtcDateTime, credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
