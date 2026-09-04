namespace NotificationPlatform.Application.Providers;

using NotificationPlatform.Domain.Common;

public interface IChannelProvider
{
    string Name { get; }
    NotificationChannel Channel { get; }
    Task<ProviderSendResult> SendAsync(ProviderSendRequest request, CancellationToken cancellationToken);
}

public sealed record ProviderSendRequest(
    Guid NotificationId,
    Guid TenantId,
    string Address,
    string? Subject,
    string Body,
    string? Category,
    string CorrelationId,
    int AttemptNumber);

public sealed record ProviderSendResult(
    ProviderResultKind Kind,
    string? ProviderMessageId,
    int LatencyMilliseconds,
    string? Error,
    TimeSpan? RetryAfter);

public interface INotificationChannel
{
    NotificationChannel Channel { get; }
    IReadOnlyList<IChannelProvider> Providers { get; }
}
