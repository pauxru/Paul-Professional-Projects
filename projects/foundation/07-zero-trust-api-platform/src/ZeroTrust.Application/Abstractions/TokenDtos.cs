namespace ZeroTrust.Application.Abstractions;

public sealed record TokenRequest(
    string GrantType,
    string? Subject,
    string? Password,
    string? ClientId,
    string? ClientSecret,
    string? Scope,
    string? Audience,
    string? Amr,
    string? Acr,
    string? RefreshToken);

public sealed record TokenResponse(
    string AccessToken,
    string TokenType,
    int ExpiresInSeconds,
    string Scope,
    string? RefreshToken,
    string? Kid,
    string? Alg);

public sealed record TokenValidationOutcome(
    bool IsValid,
    string? Subject,
    string? Audience,
    string? Issuer,
    string? Jti,
    IReadOnlyCollection<string> Scopes,
    IReadOnlyCollection<string> Roles,
    string? Amr,
    string? Acr,
    DateTime? ExpiresAtUtc,
    string? FailureReason);
