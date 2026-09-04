using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Northstar.Secrets.Application;

namespace Northstar.Secrets.Api.Auth;

public interface ITokenIssuer
{
    string Issue(string subject, IEnumerable<string> scopes, TimeSpan lifetime);
}

public sealed class LocalTokenIssuer(JwtOptions options, IClock clock) : ITokenIssuer
{
    public string Issue(string subject, IEnumerable<string> scopes, TimeSpan lifetime)
    {
        var now = clock.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new("scope", string.Join(' ', scopes.Distinct(StringComparer.Ordinal)))
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            claims,
            now.UtcDateTime,
            now.Add(lifetime).UtcDateTime,
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public static class ScopePolicies
{
    public const string ManageSecrets = nameof(ManageSecrets);
    public const string ReadValues = nameof(ReadValues);
    public const string OperateRotations = nameof(OperateRotations);
    public const string ConsumerAcknowledge = nameof(ConsumerAcknowledge);
    public const string BreakGlass = nameof(BreakGlass);
    public const string ApproveDestructive = nameof(ApproveDestructive);

    public static bool HasScope(ClaimsPrincipal principal, string requiredScope) =>
        principal.FindAll("scope")
            .SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(requiredScope, StringComparer.Ordinal);
}
