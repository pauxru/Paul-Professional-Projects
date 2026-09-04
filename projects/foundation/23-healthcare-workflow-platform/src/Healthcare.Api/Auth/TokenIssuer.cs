using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Healthcare.Api.Options;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Healthcare.Api.Auth;

public sealed class TokenIssuer
{
    private readonly JwtOptions _opts;
    public TokenIssuer(IOptions<JwtOptions> opts) => _opts = opts.Value;

    public string Issue(string userId, string displayName, IEnumerable<string> roles,
        Guid? clinicianId = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId),
            new(ClaimTypes.NameIdentifier, userId),
            new("name", displayName)
        };
        foreach (var r in roles) claims.Add(new Claim(ClaimTypes.Role, r));
        if (clinicianId is not null) claims.Add(new Claim(HttpContextCurrentUser.ClinicianIdClaim, clinicianId.Value.ToString()));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_opts.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: _opts.Issuer,
            audience: _opts.Audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddMinutes(_opts.LifetimeMinutes),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
