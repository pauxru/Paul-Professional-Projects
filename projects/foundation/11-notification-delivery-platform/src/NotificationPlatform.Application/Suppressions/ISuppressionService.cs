namespace NotificationPlatform.Application.Suppressions;

using NotificationPlatform.Domain.Common;

public sealed record SuppressionDto(Guid Id, NotificationChannel Channel, string Address, SuppressionReason Reason, string? Notes, DateTimeOffset CreatedAt);

public sealed record AddSuppressionRequest(NotificationChannel Channel, string Address, SuppressionReason Reason, string? Notes);

public interface ISuppressionService
{
    Task<IReadOnlyList<SuppressionDto>> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken ct);
    Task<SuppressionDto> AddAsync(Guid tenantId, AddSuppressionRequest request, CancellationToken ct);
    Task<bool> RemoveAsync(Guid tenantId, Guid id, CancellationToken ct);
    Task<bool> IsSuppressedAsync(Guid tenantId, NotificationChannel channel, string address, CancellationToken ct);
    Task<int> CountAsync(Guid tenantId, CancellationToken ct);
}
