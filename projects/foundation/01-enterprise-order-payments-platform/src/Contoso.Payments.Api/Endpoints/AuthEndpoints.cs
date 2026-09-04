using Contoso.Payments.Api.Startup;

namespace Contoso.Payments.Api.Endpoints;

public static class AuthEndpoints
{
    public sealed record IssueTokenRequest(string Subject, string[] Scopes, int? LifetimeMinutes);
    public sealed record IssueTokenResponse(string AccessToken, string TokenType, int ExpiresInSeconds);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Dev auth");

        // Dev-only token minter.  Guarded behind non-Production check.
        group.MapPost("/token", (IssueTokenRequest req, DevTokenIssuer issuer, IHostEnvironment env) =>
        {
            if (env.IsProduction())
                return Results.Forbid();
            var lifetime = TimeSpan.FromMinutes(req.LifetimeMinutes ?? 60);
            var token = issuer.Issue(req.Subject, req.Scopes ?? Array.Empty<string>(), lifetime);
            return Results.Ok(new IssueTokenResponse(token, "Bearer", (int)lifetime.TotalSeconds));
        });
    }
}
