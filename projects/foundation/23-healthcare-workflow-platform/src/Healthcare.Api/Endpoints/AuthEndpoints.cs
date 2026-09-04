using Healthcare.Api.Auth;
using Healthcare.Domain.Common;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Healthcare.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var g = app.MapGroup("/api/v1/auth").WithTags("Auth");

        // Dev token endpoint — issues a JWT for a caller. NOT exposed in Production.
        g.MapPost("/dev-token", Results<Ok<object>, BadRequest<string>> (DevTokenRequest req, TokenIssuer issuer, IWebHostEnvironment env) =>
        {
            if (env.IsProduction()) return TypedResults.BadRequest("Dev token issuance disabled in Production.");
            if (string.IsNullOrWhiteSpace(req.UserId)) return TypedResults.BadRequest("userId required.");
            var roles = (req.Roles is { Length: > 0 } ? req.Roles : new[] { Roles.Receptionist });
            var invalid = roles.Where(r => !Roles.All.Contains(r)).ToArray();
            if (invalid.Length > 0) return TypedResults.BadRequest($"Unknown roles: {string.Join(',', invalid)}");
            var jwt = issuer.Issue(req.UserId, req.DisplayName ?? req.UserId, roles, req.ClinicianId);
            return TypedResults.Ok<object>(new { access_token = jwt, roles });
        }).AllowAnonymous();

        g.MapGet("/whoami", (HttpContext ctx) =>
        {
            var user = ctx.User;
            return TypedResults.Ok<object>(new
            {
                authenticated = user.Identity?.IsAuthenticated ?? false,
                name = user.Identity?.Name,
                roles = user.Claims.Where(c => c.Type.EndsWith("role", StringComparison.OrdinalIgnoreCase)).Select(c => c.Value)
            });
        }).RequireAuthorization();
    }

    public sealed record DevTokenRequest(string UserId, string? DisplayName, string[]? Roles, Guid? ClinicianId);
}
