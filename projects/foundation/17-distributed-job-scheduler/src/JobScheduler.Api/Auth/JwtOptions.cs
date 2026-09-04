namespace JobScheduler.Api.Auth;

/// <summary>JWT bearer configuration. Bound from the <c>Jwt</c> configuration section.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>
    /// The insecure default key. If this value is still in use in Production the host refuses to
    /// start — signing keys must be supplied out-of-band (env var / secret store) in real deploys.
    /// </summary>
    public const string DefaultDevelopmentKey =
        "insecure-development-signing-key-change-me-0123456789abcdef";

    public string Issuer { get; set; } = "jobscheduler";
    public string Audience { get; set; } = "jobscheduler-api";
    public string SigningKey { get; set; } = DefaultDevelopmentKey;
    public int AccessTokenMinutes { get; set; } = 60;

    /// <summary>True when the signing key is still the built-in development default.</summary>
    public bool IsDefaultKey => string.Equals(SigningKey, DefaultDevelopmentKey, StringComparison.Ordinal);
}
