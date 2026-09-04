using System.Text.Json.Serialization;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;

namespace ZeroTrust.Api.Endpoints;

public sealed record TokenRequestDto(
    [property: JsonPropertyName("grant_type")] string GrantType,
    string? Subject,
    string? Password,
    [property: JsonPropertyName("client_id")] string? ClientId,
    [property: JsonPropertyName("client_secret")] string? ClientSecret,
    string? Scope,
    string? Audience,
    string? Amr,
    string? Acr,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken);

public sealed record TokenResponseDto(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    string Scope,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    string? Kid,
    string? Alg);

public sealed record OpenIdConfigurationDto(
    string Issuer,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("grant_types_supported")] string[] GrantTypesSupported,
    [property: JsonPropertyName("scopes_supported")] string[] ScopesSupported,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] string[] SigningAlgs);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/auth/token", async (
            TokenRequestDto dto,
            ITokenIssuer issuer,
            CancellationToken ct) =>
        {
            try
            {
                var response = await issuer.IssueAsync(new TokenRequest(
                    dto.GrantType, dto.Subject, dto.Password, dto.ClientId, dto.ClientSecret,
                    dto.Scope, dto.Audience, dto.Amr, dto.Acr, dto.RefreshToken), ct);
                return Results.Ok(new TokenResponseDto(response.AccessToken, response.TokenType,
                    response.ExpiresInSeconds, response.Scope, response.RefreshToken, response.Kid, response.Alg));
            }
            catch (UnauthorizedAccessException ex)
            {
                return Results.Problem(title: "invalid_request", detail: ex.Message, statusCode: 401);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Problem(title: "unsupported_grant", detail: ex.Message, statusCode: 400);
            }
        })
        .WithName("IssueToken")
        .AllowAnonymous();

        app.MapGet("/.well-known/jwks.json", async (IJwksProvider provider, CancellationToken ct) =>
        {
            var jwks = await provider.GetAsync(ct);
            return Results.Json(new
            {
                keys = jwks.Keys.Select(k => new
                {
                    kid = k.Kid, kty = k.Kty, use = k.Use, alg = k.Alg, n = k.N, e = k.E
                }).ToArray()
            });
        })
        .WithName("Jwks")
        .AllowAnonymous();

        app.MapGet("/.well-known/openid-configuration", (HttpContext http, IConfiguration cfg) =>
        {
            var issuer = cfg["Jwt:Issuer"] ?? "https://zero-trust-demo.localhost";
            var baseUrl = $"{http.Request.Scheme}://{http.Request.Host}";
            return Results.Ok(new OpenIdConfigurationDto(
                Issuer: issuer,
                TokenEndpoint: $"{baseUrl}/api/v1/auth/token",
                JwksUri: $"{baseUrl}/.well-known/jwks.json",
                GrantTypesSupported: new[] { "password", "client_credentials", "refresh_token", "service_account" },
                ScopesSupported: Scope.All.ToArray(),
                SigningAlgs: new[] { "RS256", "HS256" }));
        })
        .WithName("OpenIdConfiguration")
        .AllowAnonymous();

        return app;
    }
}
