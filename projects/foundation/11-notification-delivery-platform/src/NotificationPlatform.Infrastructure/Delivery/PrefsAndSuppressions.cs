namespace NotificationPlatform.Infrastructure.Delivery;

using Microsoft.EntityFrameworkCore;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Preferences;
using NotificationPlatform.Application.Suppressions;
using NotificationPlatform.Application.Unsubscribe;
using NotificationPlatform.Application.Options;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Domain.Preferences;
using NotificationPlatform.Domain.Suppressions;
using NotificationPlatform.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

public sealed class PreferenceService : IPreferenceService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public PreferenceService(IDbContextFactory<AppDbContext> dbFactory, IClock clock, IIdGenerator ids)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _ids = ids;
    }

    public async Task<IReadOnlyList<PreferenceDto>> ListAsync(Guid tenantId, string recipientExternalId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var recipient = await db.Recipients.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.ExternalId == recipientExternalId, ct).ConfigureAwait(false);
        if (recipient is null) return Array.Empty<PreferenceDto>();
        var list = await db.Preferences.AsNoTracking().Where(p => p.TenantId == tenantId && p.RecipientId == recipient.Id).ToListAsync(ct).ConfigureAwait(false);
        return list.Select(p => new PreferenceDto(p.Channel, p.Category, p.OptedIn, p.UpdatedAt)).ToList();
    }

    public async Task UpdateAsync(Guid tenantId, string recipientExternalId, UpdatePreferenceRequest request, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var recipient = await db.Recipients.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.ExternalId == recipientExternalId, ct).ConfigureAwait(false);
        if (recipient is null) throw new DomainException("recipient_not_found");
        var pref = await db.Preferences.SingleOrDefaultAsync(p => p.TenantId == tenantId && p.RecipientId == recipient.Id && p.Channel == request.Channel && p.Category == request.Category, ct).ConfigureAwait(false);
        if (pref is null)
        {
            pref = new RecipientPreference(_ids.NewId(), tenantId, recipient.Id, request.Channel, request.Category, request.OptedIn, _clock.UtcNow);
            db.Preferences.Add(pref);
        }
        else
        {
            pref.Set(request.OptedIn, _clock.UtcNow);
        }
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

public sealed class SuppressionService : ISuppressionService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;

    public SuppressionService(IDbContextFactory<AppDbContext> dbFactory, IClock clock, IIdGenerator ids)
    {
        _dbFactory = dbFactory;
        _clock = clock;
        _ids = ids;
    }

    public async Task<IReadOnlyList<SuppressionDto>> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var list = await db.Suppressions.AsNoTracking()
            .Where(x => x.TenantId == tenantId)
            .OrderByDescending(x => x.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct).ConfigureAwait(false);
        return list.Select(x => new SuppressionDto(x.Id, x.Channel, x.Address, x.Reason, x.Notes, x.CreatedAt)).ToList();
    }

    public async Task<int> CountAsync(Guid tenantId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Suppressions.CountAsync(x => x.TenantId == tenantId, ct).ConfigureAwait(false);
    }

    public async Task<SuppressionDto> AddAsync(Guid tenantId, AddSuppressionRequest request, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var normalized = SuppressionEntry.NormalizeAddress(request.Channel, request.Address);
        var existing = await db.Suppressions.SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Channel == request.Channel && s.Address == normalized, ct).ConfigureAwait(false);
        if (existing is not null) return new SuppressionDto(existing.Id, existing.Channel, existing.Address, existing.Reason, existing.Notes, existing.CreatedAt);
        var entity = new SuppressionEntry(_ids.NewId(), tenantId, request.Channel, request.Address, request.Reason, request.Notes, _clock.UtcNow);
        db.Suppressions.Add(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new SuppressionDto(entity.Id, entity.Channel, entity.Address, entity.Reason, entity.Notes, entity.CreatedAt);
    }

    public async Task<bool> RemoveAsync(Guid tenantId, Guid id, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.Suppressions.SingleOrDefaultAsync(s => s.TenantId == tenantId && s.Id == id, ct).ConfigureAwait(false);
        if (entity is null) return false;
        db.Suppressions.Remove(entity);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> IsSuppressedAsync(Guid tenantId, NotificationChannel channel, string address, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var normalized = SuppressionEntry.NormalizeAddress(channel, address);
        return await db.Suppressions.AnyAsync(s => s.TenantId == tenantId && s.Channel == channel && s.Address == normalized, ct).ConfigureAwait(false);
    }
}

public sealed class UnsubscribeService : IUnsubscribeService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IUnsubscribeTokenService _tokens;
    private readonly IClock _clock;
    private readonly IIdGenerator _ids;
    private readonly NotificationOptions _options;

    public UnsubscribeService(IDbContextFactory<AppDbContext> dbFactory, IUnsubscribeTokenService tokens, IClock clock, IIdGenerator ids, IOptions<NotificationOptions> options)
    {
        _dbFactory = dbFactory;
        _tokens = tokens;
        _clock = clock;
        _ids = ids;
        _options = options.Value;
    }

    public async Task<UnsubscribeIssueResult> IssueAsync(Guid tenantId, UnsubscribeIssueRequest request, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var recipient = await db.Recipients.SingleOrDefaultAsync(r => r.TenantId == tenantId && r.ExternalId == request.RecipientExternalId, ct).ConfigureAwait(false);
        if (recipient is null) throw new DomainException("recipient_not_found");
        var lifetime = TimeSpan.FromDays(_options.UnsubscribeTokenLifetimeDays);
        var now = _clock.UtcNow;
        var token = _tokens.Issue(tenantId, recipient.Id, request.Category, now, lifetime);
        return new UnsubscribeIssueResult(token, now + lifetime);
    }

    public async Task<UnsubscribeResult> HandleAsync(string token, CancellationToken ct)
    {
        var result = _tokens.Validate(token, _clock.UtcNow);
        if (!result.IsValid) return new UnsubscribeResult(false, result.Reason, null);
        if (!Enum.TryParse<NotificationCategory>(result.Category, ignoreCase: true, out var category))
            return new UnsubscribeResult(false, "unknown_category", result.Category);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Apply preferences opt-out for all channels for this category
        foreach (var channel in Enum.GetValues<NotificationChannel>())
        {
            var pref = await db.Preferences.SingleOrDefaultAsync(p => p.TenantId == result.TenantId && p.RecipientId == result.RecipientId && p.Channel == channel && p.Category == category, ct).ConfigureAwait(false);
            if (pref is null)
            {
                db.Preferences.Add(new RecipientPreference(_ids.NewId(), result.TenantId, result.RecipientId, channel, category, false, _clock.UtcNow));
            }
            else
            {
                pref.Set(false, _clock.UtcNow);
            }
        }

        // Record a suppression for email/sms addresses (best-effort)
        var recipient = await db.Recipients.SingleOrDefaultAsync(r => r.Id == result.RecipientId, ct).ConfigureAwait(false);
        if (recipient is not null && !string.IsNullOrEmpty(recipient.Email))
        {
            var normalized = SuppressionEntry.NormalizeAddress(NotificationChannel.Email, recipient.Email!);
            var already = await db.Suppressions.AnyAsync(s => s.TenantId == result.TenantId && s.Channel == NotificationChannel.Email && s.Address == normalized, ct).ConfigureAwait(false);
            if (!already)
                db.Suppressions.Add(new SuppressionEntry(_ids.NewId(), result.TenantId, NotificationChannel.Email, recipient.Email!, SuppressionReason.Unsubscribe, "one-click unsubscribe", _clock.UtcNow));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new UnsubscribeResult(true, null, result.Category);
    }
}
