namespace NotificationPlatform.Infrastructure.Providers.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NotificationPlatform.Application.Options;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Domain.Providers;
using NotificationPlatform.Infrastructure.Persistence;

public sealed class ProviderHealthTracker : IProviderHealthTracker
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly NotificationOptions _options;

    public ProviderHealthTracker(IDbContextFactory<AppDbContext> dbFactory, IOptions<NotificationOptions> options)
    {
        _dbFactory = dbFactory;
        _options = options.Value;
    }

    public async Task<CircuitState> GetStateAsync(string providerName, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ProviderHealth.AsNoTracking().SingleOrDefaultAsync(x => x.ProviderName == providerName, ct).ConfigureAwait(false);
        return entity?.State ?? CircuitState.Closed;
    }

    public async Task RecordSuccessAsync(string providerName, NotificationChannel channel, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ProviderHealth.SingleOrDefaultAsync(x => x.ProviderName == providerName, ct).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new ProviderHealth(providerName, channel);
            db.ProviderHealth.Add(entity);
        }
        entity.RecordSuccess(_options.CircuitBreakerHalfOpenSuccess);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task RecordFailureAsync(string providerName, NotificationChannel channel, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ProviderHealth.SingleOrDefaultAsync(x => x.ProviderName == providerName, ct).ConfigureAwait(false);
        if (entity is null)
        {
            entity = new ProviderHealth(providerName, channel);
            db.ProviderHealth.Add(entity);
        }
        entity.RecordFailure(_options.CircuitBreakerFailureThreshold, now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<bool> ProbeAndMaybeHalfOpenAsync(string providerName, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entity = await db.ProviderHealth.SingleOrDefaultAsync(x => x.ProviderName == providerName, ct).ConfigureAwait(false);
        if (entity is null) return true;
        var open = TimeSpan.FromSeconds(_options.CircuitBreakerOpenSeconds);
        var changed = entity.ShouldTrip(now, open);
        if (changed) await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return entity.CanSend();
    }

    public async Task<IReadOnlyList<ProviderHealth>> ListAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.ProviderHealth.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
    }
}
