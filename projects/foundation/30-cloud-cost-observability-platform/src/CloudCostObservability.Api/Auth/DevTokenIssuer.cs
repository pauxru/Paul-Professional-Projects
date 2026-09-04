using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace CloudCostObservability.Api.Auth;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required] public string Issuer { get; init; } = "CloudCostObservability";
    [Required] public string Audience { get; init; } = "CloudCostObservability.Api";
    [Required, MinLength(32)] public string SigningKey { get; init; } = "dev-only-not-a-real-secret-change-me-0123456789";
}

public sealed record DevTokenRequest(
    [property: Required] string? Subject,
    string? Scope = "finops:read",
    string? Team = null);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, string TokenType = "Bearer");

public interface ITokenIssuer
{
    TokenResponse Issue(DevTokenRequest request);
}

public sealed class DevTokenIssuer(IOptions<JwtOptions> options) : ITokenIssuer
{
    private readonly JwtOptions _options = options.Value;

    public TokenResponse Issue(DevTokenRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Subject)) throw new ArgumentException("Subject is required.", nameof(request));
        var now = DateTimeOffset.UtcNow;
        var expiry = now.AddHours(2);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, request.Subject),
            new(ClaimTypes.Name, request.Subject),
            new("scope", string.IsNullOrWhiteSpace(request.Scope) ? "finops:read" : request.Scope)
        };
        if (!string.IsNullOrWhiteSpace(request.Team))
            claims.Add(new Claim("team", request.Team.Trim().ToLowerInvariant()));
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(_options.Issuer, _options.Audience, claims, now.UtcDateTime, expiry.UtcDateTime, credentials);
        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token), expiry);
    }
}

