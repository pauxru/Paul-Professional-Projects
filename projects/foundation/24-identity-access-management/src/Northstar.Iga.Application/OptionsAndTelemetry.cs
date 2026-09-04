using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Northstar.Iga.Application;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; set; } = "Sqlite";
    [Required] public string ConnectionString { get; set; } = "Data Source=northstar-iga.db";
}

public sealed class JwtOptions
{
    public const string SectionName = "Jwt";
    [Required] public string Issuer { get; set; } = "northstar-iga-local";
    [Required] public string Audience { get; set; } = "northstar-iga-api";
    [Required, MinLength(32)] public string SigningKey { get; set; } = "dev-only-not-a-real-secret-change-me-0123456789";
    public int TokenMinutes { get; set; } = 60;
}

public sealed class LifecycleOptions
{
    public const string SectionName = "Lifecycle";
    [Range(0, 365)] public int MoverGracePeriodDays { get; set; } = 7;
    [Range(1, 10)] public int StepMaxAttempts { get; set; } = 3;
}

public sealed class GovernanceOptions
{
    public const string SectionName = "Governance";
    [Range(1, 720)] public int ApprovalSlaHours { get; set; } = 24;
    [Range(1, 720)] public int EscalatedApprovalSlaHours { get; set; } = 8;
    [Range(1, 365)] public int DormantAccountDays { get; set; } = 90;
}

public sealed class ProvisioningOptions
{
    public const string SectionName = "Provisioning";
    [Range(1, 10)] public int MaxAttempts { get; set; } = 3;
    [Range(0, 30000)] public int RetryDelayMilliseconds { get; set; } = 5;
}

public sealed class CorsOptions
{
    public const string SectionName = "Cors";
    [MinLength(1)] public string[] AllowedOrigins { get; set; } = ["http://localhost:5024"];
}

public static class IgaTelemetry
{
    public const string MeterName = "Northstar.Iga";
    public static readonly ActivitySource ActivitySource = new(MeterName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Histogram<double> DecisionLatency = Meter.CreateHistogram<double>(
        "iga.authorization.decision.duration",
        "ms",
        "Authorization decision latency.");
    public static readonly Counter<long> AuditEvents = Meter.CreateCounter<long>(
        "iga.audit.events",
        description: "Append-only audit events written.");

    private static long _pendingRequests;
    private static long _activeElevations;
    private static long _campaignItems;
    private static long _campaignCompletedItems;

    static IgaTelemetry()
    {
        Meter.CreateObservableGauge("iga.requests.pending", () => Interlocked.Read(ref _pendingRequests));
        Meter.CreateObservableGauge("iga.elevations.active", () => Interlocked.Read(ref _activeElevations));
        Meter.CreateObservableGauge(
            "iga.campaign.completion",
            () =>
            {
                var total = Interlocked.Read(ref _campaignItems);
                var completed = Interlocked.Read(ref _campaignCompletedItems);
                return total == 0 ? 0d : completed * 100d / total;
            },
            "%",
            "Percentage of certification items completed.");
    }

    public static void SetPendingRequests(long value) => Interlocked.Exchange(ref _pendingRequests, value);
    public static void SetActiveElevations(long value) => Interlocked.Exchange(ref _activeElevations, value);
    public static void SetCampaignCompletion(long completed, long total)
    {
        Interlocked.Exchange(ref _campaignCompletedItems, completed);
        Interlocked.Exchange(ref _campaignItems, total);
    }
}
