using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using FeatureFlags.Domain;

namespace FeatureFlags.Api.Contracts;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required]
    public string Provider { get; set; } = "Sqlite";
    [Required]
    public string ConnectionString { get; set; } = "Data Source=featureflags.db";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required]
    public string Issuer { get; set; } = "FeatureFlags";
    [Required]
    public string Audience { get; set; } = "FeatureFlags.Api";
    [Required, MinLength(32)]
    public string SigningKey { get; set; } = "dev-only-not-a-real-secret-change-me-0123456789";
}

public sealed class CreateProjectRequest
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string Key { get; init; } = string.Empty;
    [Required, StringLength(200, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;
}

public sealed class CreateEnvironmentRequest
{
    [Required, StringLength(100, MinimumLength = 2)]
    public string Key { get; init; } = string.Empty;
    [Required, StringLength(200, MinimumLength = 2)]
    public string Name { get; init; } = string.Empty;
    [Required, StringLength(300, MinimumLength = 12)]
    public string ServerSdkKey { get; init; } = string.Empty;
    [Required, StringLength(300, MinimumLength = 12)]
    public string ClientSdkKey { get; init; } = string.Empty;
}

public sealed class SaveFlagRequest
{
    [Required]
    public FlagDefinition? Flag { get; init; }
    [StringLength(500)]
    public string? Comment { get; init; }
    [StringLength(100)]
    public string? TicketReference { get; init; }
}

public sealed class KillSwitchRequest
{
    public bool Enabled { get; init; }
    [StringLength(500)]
    public string? Comment { get; init; }
    [StringLength(100)]
    public string? TicketReference { get; init; }
}

public sealed class EvaluationRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string FlagKey { get; init; } = string.Empty;
    [Required, StringLength(300, MinimumLength = 1)]
    public string ContextKey { get; init; } = string.Empty;
    [StringLength(100)]
    public string Kind { get; init; } = "user";
    public Dictionary<string, JsonElement>? Attributes { get; init; }
}

public sealed class PromotionRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string SourceEnvironment { get; init; } = string.Empty;
    [Required, StringLength(100, MinimumLength = 1)]
    public string TargetEnvironment { get; init; } = string.Empty;
    [StringLength(500)]
    public string? Comment { get; init; }
    [StringLength(100)]
    public string? TicketReference { get; init; }
}

public sealed class ReviewApprovalRequest
{
    public bool Approve { get; init; }
    [StringLength(500)]
    public string? Comment { get; init; }
}

public sealed class SdkEventsRequest
{
    [Required, StringLength(100, MinimumLength = 1)]
    public string ProjectKey { get; init; } = string.Empty;
    [Required, StringLength(100, MinimumLength = 1)]
    public string EnvironmentKey { get; init; } = string.Empty;
    [Required, MinLength(1)]
    public IReadOnlyList<SdkEventRequest> Events { get; init; } = Array.Empty<SdkEventRequest>();
}

public sealed class SdkEventRequest
{
    [Required, StringLength(40, MinimumLength = 1)]
    public string Kind { get; init; } = string.Empty;
    [StringLength(100)]
    public string? FlagKey { get; init; }
    public int? VariationIndex { get; init; }
    [Required, StringLength(300, MinimumLength = 1)]
    public string ContextKey { get; init; } = string.Empty;
    [StringLength(100)]
    public string? MetricKey { get; init; }
    public decimal? NumericValue { get; init; }
    public DateTimeOffset OccurredAt { get; init; }
}

public static class ApiValidation
{
    public static Dictionary<string, string[]>? Errors(object request)
    {
        var context = new ValidationContext(request);
        var results = new List<ValidationResult>();
        if (Validator.TryValidateObject(request, context, results, validateAllProperties: true))
        {
            return null;
        }

        return results.SelectMany(result => result.MemberNames.DefaultIfEmpty("request").Select(member => new { member, error = result.ErrorMessage ?? "Invalid value." }))
            .GroupBy(item => item.member, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(item => item.error).ToArray(), StringComparer.Ordinal);
    }
}
