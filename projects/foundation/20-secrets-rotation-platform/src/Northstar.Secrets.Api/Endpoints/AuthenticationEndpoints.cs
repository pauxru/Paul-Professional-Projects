using Northstar.Secrets.Api.Auth;

namespace Northstar.Secrets.Api.Endpoints;

public static class AuthenticationEndpoints
{
    public sealed record TokenRequest(string Subject, IReadOnlyList<string> Scopes);

    public static IEndpointRouteBuilder MapAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/token", (
            TokenRequest request,
            ITokenIssuer tokenIssuer,
            IHostEnvironment environment) =>
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                return Results.NotFound();
            }

            if (string.IsNullOrWhiteSpace(request.Subject) || request.Scopes.Count == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["subject"] = ["Subject and at least one scope are required."]
                });
            }

            var token = tokenIssuer.Issue(request.Subject, request.Scopes, TimeSpan.FromHours(1));
            return Results.Ok(new { accessToken = token, tokenType = "Bearer", expiresIn = 3600 });
        })
        .AllowAnonymous()
        .WithTags("Authentication");
        return app;
    }
}
