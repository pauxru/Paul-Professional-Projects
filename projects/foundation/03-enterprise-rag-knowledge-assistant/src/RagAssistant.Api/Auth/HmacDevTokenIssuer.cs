using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using RagAssistant.Domain.Documents;
using RagAssistant.Infrastructure.Options;

namespace RagAssistant.Api.Auth;

public interface IDevTokenIssuer
{
    string Issue(
        string userId,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> departments,
        Classification classification,
        TimeSpan? lifetime = null);
}

public sealed class HmacDevTokenIssuer : IDevTokenIssuer
{
    private readonly JwtOptions _options;

    public HmacDevTokenIssuer(JwtOptions options)
    {
        _options = options;
    }

    public string Issue(
        string userId,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> departments,
        Classification classification,
        TimeSpan? lifetime = null)
    {
        var handler = new JwtSecurityTokenHandler();
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new("classification", classification.ToString()),
        };
        claims.AddRange(roles.Select(r => new Claim("role", r)));
        claims.AddRange(departments.Select(d => new Claim("department", d)));

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(8)),
            signingCredentials: credentials);

        return handler.WriteToken(token);
    }
}
