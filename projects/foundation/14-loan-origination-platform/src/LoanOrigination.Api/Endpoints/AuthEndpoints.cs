using LoanOrigination.Api.Configuration;

namespace LoanOrigination.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        var group = app.MapGroup("/api/v1/auth").RequireRateLimiting("api").WithTags("Development authentication");
        group.MapPost("/token", (TokenRequest request, ITokenIssuer tokenIssuer) =>
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Development token endpoint is unavailable.");
            }

            var scopes = request.Scopes is { Count: > 0 }
                ? request.Scopes
                : new[] { ScopePolicies.Apply };
            return Results.Ok(tokenIssuer.Issue(request.Subject, scopes));
        }).AllowAnonymous();
        return app;
    }
}
