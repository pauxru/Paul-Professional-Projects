using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using SavannaLogistics.Application;

namespace SavannaLogistics.Api;

public static class ScopePolicies
{
    public static bool HasScope(ClaimsPrincipal user, string required) =>
        user.FindAll("scope")
            .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(required, StringComparer.Ordinal);
}

public sealed class TokenIssuer(IOptions<JwtOptions> options, IHostEnvironment environment, IClock clock)
{
    private static readonly IReadOnlyDictionary<string, (string Role, string Scopes)> Profiles =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["operator"] = ("dispatcher", "fleet.read fleet.write telemetry.ingest operations"),
            ["dashboard"] = ("viewer", "fleet.read operations"),
            ["simulator"] = ("device", "telemetry.ingest"),
            ["viewer"] = ("viewer", "fleet.read")
        };

    public TokenResponse Issue(string clientId)
    {
        if (environment.IsProduction())
        {
            throw new InvalidOperationException("Local token issuance is disabled in Production.");
        }

        if (!Profiles.TryGetValue(clientId, out var profile))
        {
            throw new ArgumentException("Unknown development client profile.", nameof(clientId));
        }

        var jwt = options.Value;
        var expiresAt = clock.UtcNow.AddHours(2);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, clientId),
            new Claim("name", $"{clientId} development principal"),
            new Claim("role", profile.Role),
            new Claim("scope", profile.Scopes),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            jwt.Issuer,
            jwt.Audience,
            claims,
            expires: expiresAt.UtcDateTime,
            signingCredentials: credentials);
        return new TokenResponse(new JwtSecurityTokenHandler().WriteToken(token), "Bearer", expiresAt, profile.Scopes);
    }
}

public sealed record TokenResponse(string AccessToken, string TokenType, DateTimeOffset ExpiresAt, string Scope);

public sealed class DatabaseHealthCheck(IServiceScopeFactory scopeFactory) : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    public async Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SavannaLogistics.Infrastructure.LogisticsDbContext>();
        return await db.Database.CanConnectAsync(cancellationToken)
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy("SQLite is reachable.")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("SQLite is not reachable.");
    }
}
