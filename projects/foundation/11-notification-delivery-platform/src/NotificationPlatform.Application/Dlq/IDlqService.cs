namespace NotificationPlatform.Application.Dlq;

using NotificationPlatform.Domain.Common;

public sealed record DlqItemDto(
    Guid Id,
    string TemplateKey,
    NotificationChannel Channel,
    NotificationCategory Category,
    string? LastError,
    string? LastProvider,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset? FailedAt);

public sealed record DlqReplayResult(int Replayed, int Skipped);

public interface IDlqService
{
    Task<IReadOnlyList<DlqItemDto>> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken ct);
    Task<int> CountAsync(Guid tenantId, CancellationToken ct);
    Task<DlqReplayResult> ReplayAsync(Guid tenantId, IReadOnlyList<Guid> ids, CancellationToken ct);
}
