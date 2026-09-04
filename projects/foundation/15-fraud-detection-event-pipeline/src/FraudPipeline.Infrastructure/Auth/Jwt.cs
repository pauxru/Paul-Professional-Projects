using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace FraudPipeline.Infrastructure.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "fraud-pipeline";
    public string Audience { get; set; } = "fraud-pipeline";
    public string SigningKey { get; set; } = "dev-only-not-a-real-secret-change-me-0123456789";
    public int LifetimeMinutes { get; set; } = 60;
}

public interface ITokenIssuer
{
    string Issue(string subject, IEnumerable<string> scopes);
}

public sealed class DevTokenIssuer : ITokenIssuer
{
    private readonly JwtOptions _options;
    public DevTokenIssuer(JwtOptions options) => _options = options;

    public string Issue(string subject, IEnumerable<string> scopes)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        foreach (var scope in scopes) claims.Add(new Claim("scope", scope));

        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(_options.LifetimeMinutes),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
