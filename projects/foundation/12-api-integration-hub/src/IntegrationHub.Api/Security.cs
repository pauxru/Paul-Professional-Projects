using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace IntegrationHub.Api;

public static class ScopeAuthorization
{
    public static bool HasScope(ClaimsPrincipal user, string required) =>
        user.FindAll("scope").Concat(user.FindAll("scp"))
            .SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Contains(required, StringComparer.OrdinalIgnoreCase);
}

public sealed class TokenIssuer(IOptions<JwtOptions> options)
{
    public string Issue(string subject, IReadOnlyCollection<string> scopes, TimeSpan lifetime)
    {
        var settings = options.Value;
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            settings.Issuer,
            settings.Audience,
            [
                new Claim(JwtRegisteredClaimNames.Sub, subject),
                new Claim("scope", string.Join(' ', scopes)),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            ],
            DateTime.UtcNow,
            DateTime.UtcNow.Add(lifetime),
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var correlationId = context.Request.Headers.TryGetValue("X-Correlation-Id", out var incoming)
                            && !string.IsNullOrWhiteSpace(incoming)
            ? incoming.ToString()[..Math.Min(incoming.ToString().Length, 128)]
            : Guid.NewGuid().ToString("N");
        context.TraceIdentifier = correlationId;
        context.Response.Headers["X-Correlation-Id"] = correlationId;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }
}

public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            return Task.CompletedTask;
        });
        await next(context);
    }
}
