using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace Collab.Api.Auth;

/// <summary>
/// Options for signing and validating JWTs. Bound from the <c>Jwt</c> configuration section. The key
/// ships with a development default; in production it MUST come from a secret (see .env.example).
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public string Key { get; set; } = string.Empty;
    public string Issuer { get; set; } = "collab";
    public string Audience { get; set; } = "collab-clients";
    public int LifetimeHours { get; set; } = 8;
}

/// <summary>
/// Issues signed JWTs for the demo/login endpoint. Password-less by design — this is a portfolio demo
/// whose engineering story is realtime collaboration, not identity. The token carries the user id
/// (<c>sub</c>), email and display name; all resource authorization happens server-side per request.
/// </summary>
public sealed class JwtTokenService(Microsoft.Extensions.Options.IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    public (string Token, DateTimeOffset ExpiresAt) Create(Guid userId, string email, string displayName)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.Key)),
            SecurityAlgorithms.HmacSha256);

        var expiresAt = DateTimeOffset.UtcNow.AddHours(_options.LifetimeHours);
        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims:
            [
                new Claim("sub", userId.ToString()),
                new Claim("email", email),
                new Claim("name", displayName),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
            ],
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
