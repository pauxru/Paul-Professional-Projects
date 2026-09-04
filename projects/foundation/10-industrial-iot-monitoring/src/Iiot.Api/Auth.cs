using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;

namespace Iiot.Api;

public static class Policies
{
    public const string Operator = "operator";
    public const string Admin = "admin";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const string DefaultSigningKey = "dev-only-not-a-real-secret-change-me-0123456789";

    [Required]
    public string Issuer { get; init; } = "industrial-iot-monitoring";

    [Required]
    public string Audience { get; init; } = "industrial-iot-dashboard";

    [Required]
    [MinLength(32)]
    public string SigningKey { get; init; } = DefaultSigningKey;

    public static byte[] GetKey(string key) => Encoding.UTF8.GetBytes(key);
}

public interface ITokenIssuer
{
    string Issue(string subject, string scope);
}

public sealed class JwtTokenIssuer(IOptions<JwtOptions> configuredOptions) : ITokenIssuer
{
    public string Issue(string subject, string scope)
    {
        var options = configuredOptions.Value;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, subject),
            new Claim("scope", scope)
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(JwtOptions.GetKey(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            claims,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed record TokenRequest([property: Required] string Subject, [property: Required] string Scope);

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/api/v1/auth/token", (TokenRequest request, ITokenIssuer issuer, IHostEnvironment environment) =>
        {
            if (environment.IsProduction())
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Subject) || string.IsNullOrWhiteSpace(request.Scope))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["request"] = ["subject and scope are required."] });
            }

            return Results.Ok(new { accessToken = issuer.Issue(request.Subject, request.Scope), tokenType = "Bearer" });
        }).AllowAnonymous();
    }
}
