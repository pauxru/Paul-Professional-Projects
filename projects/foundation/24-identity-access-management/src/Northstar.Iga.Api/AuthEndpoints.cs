using Microsoft.Extensions.Options;
using Northstar.Iga.Application;

namespace Northstar.Iga.Api;

public static class AuthEndpoints
{
    private static readonly HashSet<string> AllowedScopes =
        new([ApiPolicies.Read, ApiPolicies.Admin, ApiPolicies.Approve], StringComparer.OrdinalIgnoreCase);

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/token", (
            TokenRequest request,
            ITokenIssuer tokenIssuer,
            IOptions<JwtOptions> jwtOptions,
            IWebHostEnvironment environment) =>
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                return Results.NotFound();
            }
            if (string.IsNullOrWhiteSpace(request.Subject))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["subject"] = ["Subject is required."]
                });
            }
            var scopes = request.Scopes.Where(AllowedScopes.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (scopes.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["scopes"] = ["At least one supported scope is required."]
                });
            }

            var expiresIn = TimeSpan.FromMinutes(jwtOptions.Value.TokenMinutes);
            return Results.Ok(new
            {
                accessToken = tokenIssuer.Issue(request.Subject, scopes, expiresIn),
                tokenType = "Bearer",
                expiresIn = (int)expiresIn.TotalSeconds,
                scope = string.Join(' ', scopes)
            });
        }).AllowAnonymous().WithTags("Authentication");
        return app;
    }
}
