using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using RagAssistant.Api.Auth;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Api.Endpoints;

public sealed record DevTokenRequest(string UserId, IReadOnlyList<string>? Roles, IReadOnlyList<string>? Departments, string? Classification);

public sealed record DevTokenResponse(string AccessToken, DateTimeOffset ExpiresAt);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Auth");
        group.MapPost("/token", IssueTokenAsync).WithName("IssueToken").AllowAnonymous();
        return app;
    }

    private static Task<Results<Ok<DevTokenResponse>, ValidationProblem>> IssueTokenAsync(
        [FromBody] DevTokenRequest request,
        IDevTokenIssuer issuer,
        IWebHostEnvironment env,
        CancellationToken ct)
    {
        if (env.IsProduction())
        {
            return Task.FromResult<Results<Ok<DevTokenResponse>, ValidationProblem>>(TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["environment"] = ["Dev token issuance is disabled in Production."],
            }));
        }

        if (request is null || string.IsNullOrWhiteSpace(request.UserId))
        {
            return Task.FromResult<Results<Ok<DevTokenResponse>, ValidationProblem>>(TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["userId"] = ["User id is required."],
            }));
        }

        var classification = Classification.Internal;
        if (!string.IsNullOrWhiteSpace(request.Classification)
            && Enum.TryParse<Classification>(request.Classification, ignoreCase: true, out var parsed))
        {
            classification = parsed;
        }

        var lifetime = TimeSpan.FromHours(8);
        var token = issuer.Issue(request.UserId, request.Roles ?? [], request.Departments ?? [], classification, lifetime);
        return Task.FromResult<Results<Ok<DevTokenResponse>, ValidationProblem>>(
            TypedResults.Ok(new DevTokenResponse(token, DateTimeOffset.UtcNow.Add(lifetime))));
    }
}
