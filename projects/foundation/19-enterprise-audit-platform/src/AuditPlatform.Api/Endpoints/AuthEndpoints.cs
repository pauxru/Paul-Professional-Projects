using AuditPlatform.Api.Auth;
using AuditPlatform.Api.Options;

namespace AuditPlatform.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, IHostEnvironment env)
    {
        // Dev-only token minter for exercising the API without a full OIDC provider.
        app.MapPost("/api/v1/auth/dev-token", (TokenRequest body, JwtOptions jwt) =>
        {
            var scopes = string.IsNullOrWhiteSpace(body.Scopes) ? new[] { "audit:read" } : body.Scopes.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var token = DevTokenIssuer.Issue(jwt, body.Subject, body.TenantId, scopes, body.Clearance ?? "standard");
            return Results.Ok(new { access_token = token, token_type = "Bearer", expires_in = 7200 });
        })
        .AllowAnonymous()
        .WithTags("auth");

        return app;
    }
}

public sealed record TokenRequest(string Subject, string TenantId, string Scopes, string? Clearance);
