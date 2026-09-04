using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace LoanOrigination.Api.Configuration;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; init; } = "Sqlite";

    [Required]
    public string ConnectionString { get; init; } = "Data Source=loan-origination.db";
}

public sealed class ObjectStorageOptions
{
    public const string SectionName = "ObjectStorage";

    [Required]
    public string Provider { get; init; } = "LocalFileSystem";

    [Required]
    public string RootPath { get; init; } = "data\\objects";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const string DefaultSigningKey = "dev-only-not-a-real-secret-change-me-0123456789";

    [Required]
    public string Issuer { get; init; } = "rift-valley-credit-local";

    [Required]
    public string Audience { get; init; } = "loan-origination-api";

    [Required, MinLength(32)]
    public string SigningKey { get; init; } = DefaultSigningKey;
}

public sealed class WebhookOptions
{
    public const string SectionName = "Webhook";
    public const string DefaultSigningSecret = "dev-only-webhook-secret-not-for-production-0123456789";

    [Required, MinLength(32)]
    public string SigningSecret { get; init; } = DefaultSigningSecret;

    [Range(1, 30)]
    public int TimestampWindowMinutes { get; init; } = 5;
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";

    [MinLength(1)]
    public string[] AllowedOrigins { get; init; } = ["http://localhost:5014"];
}

public sealed record TokenRequest(string Subject, IReadOnlyList<string>? Scopes);

public sealed record TokenResponse(string AccessToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes);

public interface ITokenIssuer
{
    TokenResponse Issue(string subject, IReadOnlyList<string> scopes);
}

public sealed class LocalJwtTokenIssuer(JwtOptions options, TimeProvider timeProvider) : ITokenIssuer
{
    public TokenResponse Issue(string subject, IReadOnlyList<string> scopes)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            throw new ArgumentException("A token subject is required.", nameof(subject));
        }

        var now = timeProvider.GetUtcNow();
        var expires = now.AddHours(2);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim(ClaimTypes.Name, subject),
            new Claim("scope", string.Join(' ', scopes.Distinct(StringComparer.OrdinalIgnoreCase)))
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims, now.UtcDateTime, expires.UtcDateTime, credentials);
        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token), expires, scopes);
    }
}

public static class ScopePolicies
{
    public const string Apply = "loans:apply";
    public const string Underwrite = "loans:underwrite";
    public const string Approve = "loans:approve";
    public const string Admin = "loans:admin";

    public static bool HasScope(ClaimsPrincipal user, string requiredScope) =>
        user.Claims
            .Where(claim => claim.Type is "scope" or "scp")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Contains(requiredScope, StringComparer.OrdinalIgnoreCase);
}
