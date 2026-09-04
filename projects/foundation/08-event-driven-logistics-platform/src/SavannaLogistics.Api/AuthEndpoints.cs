namespace SavannaLogistics.Api;

public sealed record TokenRequest(string ClientId);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth").WithTags("Authentication");
        group.MapPost("/token", (TokenRequest request, TokenIssuer issuer) =>
        {
            if (string.IsNullOrWhiteSpace(request.ClientId))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["clientId"] = ["A development client profile is required."]
                });
            }

            try
            {
                return Results.Ok(issuer.Issue(request.ClientId));
            }
            catch (ArgumentException exception)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["clientId"] = [exception.Message]
                });
            }
        }).AllowAnonymous();
        return endpoints;
    }
}
