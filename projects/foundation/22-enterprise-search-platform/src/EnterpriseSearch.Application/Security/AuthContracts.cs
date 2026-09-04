using System.ComponentModel.DataAnnotations;

namespace EnterpriseSearch.Application.Security;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const string DefaultDevelopmentSigningKey = "dev-only-not-a-real-secret-change-me-0123456789";
    [Required] public string Issuer { get; init; } = "enterprise-search";
    [Required] public string Audience { get; init; } = "enterprise-search-clients";
    [Required, MinLength(32)] public string SigningKey { get; init; } = DefaultDevelopmentSigningKey;
    [Range(1, 120)] public int LifetimeMinutes { get; init; } = 60;
}

public sealed record TokenIssueRequest(string Subject, IReadOnlyCollection<string> Scopes, IReadOnlyCollection<string> Groups);
public interface ITokenIssuer
{
    string Issue(TokenIssueRequest request);
}
