namespace NotificationPlatform.Application.Templates;

using NotificationPlatform.Domain.Common;

public sealed record CreateTemplateRequest(
    string TemplateKey,
    NotificationChannel Channel,
    string Locale,
    NotificationCategory Category,
    string? Subject,
    string Body,
    bool StrictMode);

public sealed record TemplateDto(
    Guid Id,
    string TemplateKey,
    NotificationChannel Channel,
    string Locale,
    int Version,
    string? Subject,
    string Body,
    NotificationCategory Category,
    bool StrictMode,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record TemplatePreviewRequest(string TemplateKey, NotificationChannel Channel, string? Locale, System.Text.Json.JsonElement Payload);

public sealed record TemplatePreviewResult(string? Subject, string Body, IReadOnlyList<string> UnknownTokens);

public interface ITemplateService
{
    Task<TemplateDto> CreateAsync(Guid tenantId, CreateTemplateRequest request, CancellationToken cancellationToken);
    Task<TemplateDto?> GetLatestAsync(Guid tenantId, string templateKey, NotificationChannel channel, string locale, CancellationToken cancellationToken);
    Task<IReadOnlyList<TemplateDto>> ListAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<TemplatePreviewResult> PreviewAsync(Guid tenantId, string tenantDefaultLocale, TemplatePreviewRequest request, CancellationToken cancellationToken);
}
