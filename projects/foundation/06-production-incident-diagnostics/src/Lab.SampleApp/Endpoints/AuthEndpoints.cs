using System.ComponentModel.DataAnnotations;
using Lab.Application.Abstractions;
using Lab.Application.Contracts;

namespace Lab.SampleApp.Endpoints;

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/auth/token", (TokenRequest request, ITokenIssuer issuer) =>
        {
            var errors = Validation.Validate(request);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var scopes = request.Scope!
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            var accessToken = issuer.Issue(request.Subject!, scopes);
            return Results.Ok(new TokenResponse(accessToken, "Bearer", 3_600));
        })
        .WithTags("Authentication")
        .WithName("IssueDevelopmentToken");

        return endpoints;
    }
}

internal static class Validation
{
    public static Dictionary<string, string[]> Validate(object model)
    {
        var context = new ValidationContext(model);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);
        return results
            .SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty)
                .Select(member => new { Member = member, Message = result.ErrorMessage ?? "The value is invalid." }))
            .GroupBy(x => x.Member, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.Select(x => x.Message).ToArray(),
                StringComparer.Ordinal);
    }
}
