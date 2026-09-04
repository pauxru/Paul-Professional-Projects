namespace NotificationPlatform.Application.Notifications;

using NotificationPlatform.Domain.Common;

public interface INotificationService
{
    Task<SendOutcome> QueueAsync(Guid tenantId, SendNotificationRequest request, string correlationId, CancellationToken cancellationToken);
    Task<BulkSendOutcome> QueueBulkAsync(Guid tenantId, BulkSendRequest request, string correlationId, CancellationToken cancellationToken);
    Task<NotificationDto?> GetAsync(Guid tenantId, Guid notificationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationDto>> ListAsync(Guid tenantId, int page, int pageSize, NotificationStatus? status, CancellationToken cancellationToken);
    Task<int> CountAsync(Guid tenantId, NotificationStatus? status, CancellationToken cancellationToken);
}

public interface IDeliveryPipeline
{
    Task<int> ProcessDueAsync(int batchSize, CancellationToken cancellationToken);
    Task<int> ProcessTenantAsync(Guid tenantId, int batchSize, CancellationToken cancellationToken);
    Task<int> ScanScheduledAsync(CancellationToken cancellationToken);
}
