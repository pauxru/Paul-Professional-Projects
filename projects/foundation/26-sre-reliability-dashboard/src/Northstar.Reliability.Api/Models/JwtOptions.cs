using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using Northstar.Reliability.Application.Abstractions;

namespace Northstar.Reliability.Api.Models;

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    public const string DevelopmentSigningKey = "dev-only-not-a-real-secret-change-me-0123456789";

    [Required]
    public string Issuer { get; set; } = "northstar-reliability";

    [Required]
    public string Audience { get; set; } = "northstar-reliability-api";

    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = DevelopmentSigningKey;
}

public sealed class JwtTokenIssuer(JwtOptions options) : ITokenIssuer
{
    public string Issue(string subject, IEnumerable<string> scopes)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, subject),
            new(ClaimTypes.Name, subject),
            new("scope", string.Join(' ', scopes.Distinct(StringComparer.OrdinalIgnoreCase)))
        };
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.SigningKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            options.Issuer,
            options.Audience,
            claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.AddHours(8),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

}

public sealed class ErrorBudgetPolicyOptions
{
    public const string SectionName = "ErrorBudgetPolicy";

    [Range(0.01, 100)]
    public decimal WarningRemainingPercent { get; set; } = 50m;

    [Range(0.01, 99.99)]
    public decimal RiskyDeployFreezeRemainingPercent { get; set; } = 25m;
}

public sealed class MetricRetentionOptions
{
    public const string SectionName = "MetricRetention";

    [Range(1, 8_760)]
    public int RawRetentionHours { get; set; } = 48;

    [Range(1, 3_650)]
    public int HourlyRetentionDays { get; set; } = 90;
}

public sealed class BurnRateAlertOptions
{
    public const string SectionName = "BurnRateAlerts";

    [Range(1, 10_080)]
    public int FastPageLongWindowMinutes { get; set; } = 60;

    [Range(1, 10_080)]
    public int FastPageShortWindowMinutes { get; set; } = 5;

    [Range(0.01, 10_000)]
    public decimal FastPageLongThreshold { get; set; } = 14.4m;

    [Range(0.01, 10_000)]
    public decimal FastPageShortThreshold { get; set; } = 6m;

    [Range(1, 10_080)]
    public int SlowPageLongWindowMinutes { get; set; } = 360;

    [Range(1, 10_080)]
    public int SlowPageShortWindowMinutes { get; set; } = 30;

    [Range(0.01, 10_000)]
    public decimal SlowPageLongThreshold { get; set; } = 6m;

    [Range(0.01, 10_000)]
    public decimal SlowPageShortThreshold { get; set; } = 3m;

    [Range(1, 10_080)]
    public int SustainedTicketLongWindowMinutes { get; set; } = 4_320;

    [Range(1, 10_080)]
    public int SustainedTicketShortWindowMinutes { get; set; } = 1_440;

    [Range(0.01, 10_000)]
    public decimal SustainedTicketLongThreshold { get; set; } = 1m;

    [Range(0.01, 10_000)]
    public decimal SustainedTicketShortThreshold { get; set; } = 3m;
}
