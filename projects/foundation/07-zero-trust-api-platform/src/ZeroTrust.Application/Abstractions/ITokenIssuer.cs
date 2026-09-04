namespace ZeroTrust.Application.Abstractions;

/// <summary>
/// Local, self-contained OAuth2-style token issuer. In a production configuration this
/// would be replaced by Entra ID / Auth0 through the same interface.
/// </summary>
public interface ITokenIssuer
{
    Task<TokenResponse> IssueAsync(TokenRequest request, CancellationToken ct);
    Task RevokeAsync(string jti, string subject, DateTime expiresAtUtc, string reason, CancellationToken ct);
}

public interface ITokenValidator
{
    Task<TokenValidationOutcome> ValidateAsync(string bearerToken, string expectedAudience, CancellationToken ct);
}
