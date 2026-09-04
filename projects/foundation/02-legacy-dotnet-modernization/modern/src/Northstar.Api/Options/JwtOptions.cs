using System.ComponentModel.DataAnnotations;

namespace Northstar.Api.Options;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; init; } = "northstar-modernization";

    [Required]
    public string Audience { get; init; } = "northstar-claims-api";

    [Required]
    [MinLength(32)]
    public string SigningKey { get; init; } = "dev-only-not-a-real-secret-change-me-0123456789";
}
