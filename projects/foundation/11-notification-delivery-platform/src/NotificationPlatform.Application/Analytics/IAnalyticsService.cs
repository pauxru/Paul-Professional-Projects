namespace NotificationPlatform.Application.Analytics;

using NotificationPlatform.Domain.Common;

public sealed record TenantAnalyticsSummary(
    int Total,
    int Delivered,
    int Bounced,
    int Failed,
    int Suppressed,
    int DeadLettered,
    double DeliveryRate,
    double BounceRate,
    int P50LatencyMs,
    int P95LatencyMs,
    int P99LatencyMs,
    IReadOnlyDictionary<string, int> ByProvider,
    IReadOnlyDictionary<NotificationChannel, int> ByChannel);

public sealed record ProviderHealthDto(string ProviderName, NotificationChannel Channel, CircuitState State, int ConsecutiveFailures, DateTimeOffset LastFailureAt);

public interface IAnalyticsService
{
    Task<TenantAnalyticsSummary> GetSummaryAsync(Guid tenantId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct);
    Task<IReadOnlyList<ProviderHealthDto>> GetProviderHealthAsync(CancellationToken ct);
    Task<QueueSnapshot> GetQueueSnapshotAsync(CancellationToken ct);
}

public sealed record QueueSnapshot(int Queued, int Scheduled, int Rendering, int Dispatched, int Sent, int Delivered, int Bounced, int Failed, int Suppressed, int DeadLettered);
