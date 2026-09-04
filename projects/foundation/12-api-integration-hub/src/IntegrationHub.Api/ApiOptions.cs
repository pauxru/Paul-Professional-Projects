using System.ComponentModel.DataAnnotations;

namespace IntegrationHub.Api;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; init; } = "IntegrationHub.Local";

    [Required]
    public string Audience { get; init; } = "IntegrationHub.Api";

    [Required, MinLength(32)]
    public string SigningKey { get; init; } = "dev-only-not-a-real-secret-change-me-0123456789";
}

public sealed class ApiSecurityOptions
{
    public const string SectionName = "ApiSecurity";

    [Required]
    public string DemoClientId { get; init; } = "demo-client";

    [Required]
    public string DemoClientSecret { get; init; } = "dev-only-client-secret";
}

public static class Policies
{
    public const string Read = "Hub.Read";
    public const string Write = "Hub.Write";
    public const string Admin = "Hub.Admin";
}
