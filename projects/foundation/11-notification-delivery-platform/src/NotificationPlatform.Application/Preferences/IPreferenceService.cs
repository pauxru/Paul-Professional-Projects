namespace NotificationPlatform.Application.Preferences;

using NotificationPlatform.Domain.Common;

public sealed record PreferenceDto(NotificationChannel Channel, NotificationCategory Category, bool OptedIn, DateTimeOffset UpdatedAt);

public sealed record UpdatePreferenceRequest(NotificationChannel Channel, NotificationCategory Category, bool OptedIn);

public interface IPreferenceService
{
    Task<IReadOnlyList<PreferenceDto>> ListAsync(Guid tenantId, string recipientExternalId, CancellationToken ct);
    Task UpdateAsync(Guid tenantId, string recipientExternalId, UpdatePreferenceRequest request, CancellationToken ct);
}
