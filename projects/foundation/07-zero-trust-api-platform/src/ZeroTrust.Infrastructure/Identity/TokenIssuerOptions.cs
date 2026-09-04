namespace ZeroTrust.Infrastructure.Identity;

public sealed class TokenIssuerOptions
{
    public const string SectionName = "Jwt";
    public string Issuer { get; set; } = "https://zero-trust-demo.localhost";
    public int AccessTokenMinutes { get; set; } = 15;
    public int AdminAccessTokenMinutes { get; set; } = 5;
    public int RefreshTokenDays { get; set; } = 14;
    public string HsSigningKey { get; set; } = "dev-only-not-a-real-secret-change-me-0123456789";
    public bool UseRs256 { get; set; } = true;
    public string DefaultAudience { get; set; } = "ntsf-customer-api";
    public bool RequireStepUpForAdmin { get; set; } = true;
    public string RequiredAmr { get; set; } = "mfa";
    public string RequiredAcr { get; set; } = "urn:ntsf:acr:step-up";
}
