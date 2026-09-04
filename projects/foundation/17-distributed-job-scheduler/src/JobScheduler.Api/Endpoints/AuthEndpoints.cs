using JobScheduler.Api.Auth;
using JobScheduler.Api.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace JobScheduler.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Development/demo token minting. Guarded so it cannot be used in Production.
        app.MapPost("/api/v1/auth/token", IssueAsync).WithTags("Auth").AllowAnonymous();
    }

    private static Results<Ok<TokenResponse>, ProblemHttpResult> IssueAsync(
        TokenRequest? request, TokenIssuer issuer, IHostEnvironment env)
    {
        if (env.IsProduction())
        {
            return TypedResults.Problem(
                "The development token endpoint is disabled in Production. Integrate a real identity provider.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        var subject = string.IsNullOrWhiteSpace(request?.Subject) ? "dev-user" : request!.Subject!;
        var scopes = request?.Scopes is { Length: > 0 } s
            ? s.Where(x => AuthConstants.AllScopes.Contains(x)).ToArray()
            : AuthConstants.AllScopes.ToArray();

        var (token, expiresAt) = issuer.Issue(subject, scopes);
        return TypedResults.Ok(new TokenResponse(token, "Bearer", expiresAt, scopes));
    }
}
