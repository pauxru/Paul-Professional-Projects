using System.ComponentModel.DataAnnotations;
using Northstar.Application.Abstractions;

namespace Northstar.Api.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/auth").WithTags("Authentication").RequireRateLimiting("api");
        group.MapPost("/token", (TokenRequest request, ITokenIssuer issuer, IHostEnvironment environment) =>
        {
            var errors = Validation.Errors(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest);
            }

            if (environment.IsProduction())
            {
                return Results.NotFound();
            }

            var allowed = new HashSet<string>(["claims:read", "claims:adjust", "claims:approve"], StringComparer.Ordinal);
            var scopes = request.Scopes.Where(allowed.Contains).Distinct(StringComparer.Ordinal).ToArray();
            return Results.Ok(new { accessToken = issuer.Issue(request.Subject, scopes), tokenType = "Bearer", scopes });
        }).AllowAnonymous();
        return app;
    }
}

public sealed class TokenRequest
{
    [Required]
    [MinLength(3)]
    [StringLength(200)]
    public string Subject { get; init; } = string.Empty;

    [MinLength(1)]
    public string[] Scopes { get; init; } = [];
}
