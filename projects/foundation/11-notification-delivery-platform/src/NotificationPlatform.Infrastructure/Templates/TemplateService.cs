namespace NotificationPlatform.Infrastructure.Templates;

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Localization;
using NotificationPlatform.Application.Templates;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Domain.Templates;
using NotificationPlatform.Infrastructure.Persistence;

public sealed class TemplateService : ITemplateService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITemplateEngine _engine;
    private readonly ILocaleResolver _locales;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public TemplateService(IDbContextFactory<AppDbContext> dbFactory, ITemplateEngine engine, ILocaleResolver locales, IClock clock, IIdGenerator ids)
    {
        _dbFactory = dbFactory;
        _engine = engine;
        _locales = locales;
        _clock = clock;
        _ids = ids;
    }

    public async Task<TemplateDto> CreateAsync(Guid tenantId, CreateTemplateRequest request, CancellationToken ct)
    {
        if (!_engine.ValidateSyntax(request.Body, out var err))
            throw new DomainException($"Template body invalid: {err}");
        if (!string.IsNullOrEmpty(request.Subject) && !_engine.ValidateSyntax(request.Subject!, out var errS))
            throw new DomainException($"Template subject invalid: {errS}");

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var existing = await db.Templates
            .Where(x => x.TenantId == tenantId
                        && x.TemplateKey == request.TemplateKey
                        && x.Channel == request.Channel
                        && x.Locale == request.Locale)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        var nextVersion = (existing?.Version ?? 0) + 1;

        var entity = new NotificationTemplate(
            _ids.NewId(),
            tenantId,
            request.TemplateKey,
            request.Channel,
            request.Locale,
            nextVersion,
            request.Subject,
            request.Body,
            request.Category,
            request.StrictMode,
            true,
            _clock.UtcNow);

        db.Templates.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Map(entity);
    }

    public async Task<TemplateDto?> GetLatestAsync(Guid tenantId, string templateKey, NotificationChannel channel, string locale, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Templates
            .Where(x => x.TenantId == tenantId
                        && x.TemplateKey == templateKey
                        && x.Channel == channel
                        && x.Locale == locale
                        && x.IsActive)
            .OrderByDescending(x => x.Version)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<TemplateDto>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var list = await db.Templates.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderBy(x => x.TemplateKey).ThenBy(x => x.Channel).ThenBy(x => x.Locale).ThenByDescending(x => x.Version)
            .ToListAsync(ct).ConfigureAwait(false);
        return list.Select(Map).ToList();
    }

    public async Task<TemplatePreviewResult> PreviewAsync(Guid tenantId, string tenantDefaultLocale, TemplatePreviewRequest request, CancellationToken ct)
    {
        var template = await ResolveWithFallbackAsync(tenantId, request.TemplateKey, request.Channel, request.Locale, tenantDefaultLocale, ct).ConfigureAwait(false);
        if (template is null) throw new DomainException($"No template found for '{request.TemplateKey}'/{request.Channel}.");
        var bodyResult = _engine.Render(template.Body, request.Payload, template.StrictMode);
        string? subject = null;
        if (!string.IsNullOrEmpty(template.Subject))
            subject = _engine.Render(template.Subject!, request.Payload, template.StrictMode).Body;
        return new TemplatePreviewResult(subject, bodyResult.Body, bodyResult.UnknownTokens);
    }

    public async Task<NotificationTemplate?> ResolveWithFallbackAsync(Guid tenantId, string templateKey, NotificationChannel channel, string? requestedLocale, string tenantDefault, CancellationToken ct)
    {
        var chain = _locales.ResolveChain(requestedLocale, tenantDefault);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var locale in chain)
        {
            var entity = await db.Templates.AsNoTracking()
                .Where(x => x.TenantId == tenantId
                            && x.TemplateKey == templateKey
                            && x.Channel == channel
                            && x.Locale == locale
                            && x.IsActive)
                .OrderByDescending(x => x.Version)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (entity is not null) return entity;
        }
        return null;
    }

    private static TemplateDto Map(NotificationTemplate t) => new(
        t.Id, t.TemplateKey, t.Channel, t.Locale, t.Version, t.Subject, t.Body, t.Category, t.StrictMode, t.IsActive, t.CreatedAt);
}
