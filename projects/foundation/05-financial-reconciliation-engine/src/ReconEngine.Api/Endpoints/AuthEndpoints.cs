using ReconEngine.Api.Auth;
using ReconEngine.Api.Contracts;

namespace ReconEngine.Api.Endpoints;

public static class AuthEndpoints
{
    /// <summary>
    /// Dev/test token endpoint. Issues a signed JWT carrying the requested scopes so the API can be
    /// exercised end-to-end without an external identity provider. Not intended for production use.
    /// </summary>
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");

        group.MapPost("/token", (TokenRequest request, TokenIssuer issuer) =>
        {
            var scopes = request.Scopes is { Length: > 0 } ? request.Scopes : ReconScopes.All;
            var (token, expires) = issuer.Issue(request.Subject ?? "demo-user", scopes);
            return Results.Ok(new TokenResponse(token, expires));
        })
        .AllowAnonymous()
        .WithName("IssueToken")
        .WithSummary("Issue a development JWT with the requested scopes.");

        return app;
    }
}
