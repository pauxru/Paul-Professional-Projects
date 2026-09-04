using Lakehouse.Api.Auth;

namespace Lakehouse.Api.Endpoints;

/// <summary>
/// Development/test token issuance. In production this endpoint would not exist — clients would obtain
/// tokens from the real identity provider. Documented as such in the security review.
/// </summary>
public static class AuthEndpoints
{
    public sealed record TokenRequest(string? Role);
    public sealed record TokenResponse(string Token, string Role, int ExpiresInSeconds);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/auth/token", (TokenRequest? body, DevTokenService tokens) =>
        {
            var role = string.IsNullOrWhiteSpace(body?.Role) ? AuthPolicies.ReaderRole : body!.Role!.Trim().ToLowerInvariant();
            if (role != AuthPolicies.ReaderRole && role != AuthPolicies.OperatorRole)
                return Results.BadRequest(new { error = $"Unknown role '{role}'. Use 'reader' or 'operator'." });

            var token = tokens.Issue(role);
            return Results.Ok(new TokenResponse(token, role, 3600));
        })
        .AllowAnonymous()
        .WithName("IssueDevToken")
        .WithTags("Auth");

        return app;
    }
}
