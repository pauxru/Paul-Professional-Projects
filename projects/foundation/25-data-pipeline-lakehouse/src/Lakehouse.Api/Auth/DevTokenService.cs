using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Lakehouse.Api.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace Lakehouse.Api.Auth;

/// <summary>Authorization policy names and the roles that satisfy them.</summary>
public static class AuthPolicies
{
    public const string Reader = "reader";
    public const string Operator = "operator";

    public const string ReaderRole = "reader";
    public const string OperatorRole = "operator";
}

/// <summary>
/// Issues short-lived HS256 JWTs for local development and tests. A real deployment would federate to
/// Entra ID / an OIDC provider — this exists purely so the auth pipeline (401/403) is exercised without
/// external infrastructure. The signing key is a dev secret and must never be a production key.
/// </summary>
public sealed class DevTokenService(LakehouseOptions options)
{
    public string Issue(string role, TimeSpan? lifetime = null)
    {
        // Operators are also readers; readers are read-only.
        var roles = role.Equals(AuthPolicies.OperatorRole, StringComparison.OrdinalIgnoreCase)
            ? new[] { AuthPolicies.ReaderRole, AuthPolicies.OperatorRole }
            : new[] { AuthPolicies.ReaderRole };

        var claims = new List<Claim> { new(JwtRegisteredClaimNames.Sub, $"dev-{role}") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.Auth.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: options.Auth.Issuer,
            audience: options.Auth.Audience,
            claims: claims,
            expires: DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromHours(1)),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
