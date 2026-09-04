using FraudPipeline.Api.Contracts;
using FraudPipeline.Infrastructure.Auth;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace FraudPipeline.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");
        group.MapPost("/token", IssueToken).AllowAnonymous();
        return app;
    }

    private static Results<Ok<TokenResponse>, BadRequest<ProblemDetails>> IssueToken(
        [FromBody] TokenRequest request,
        ITokenIssuer issuer)
    {
        if (string.IsNullOrWhiteSpace(request.Subject) || request.Scopes is null || request.Scopes.Length == 0)
            return TypedResults.BadRequest(new ProblemDetails { Title = "Missing subject or scopes" });
        var token = issuer.Issue(request.Subject, request.Scopes);
        return TypedResults.Ok(new TokenResponse(token));
    }
}

public sealed record TokenResponse(string AccessToken);
