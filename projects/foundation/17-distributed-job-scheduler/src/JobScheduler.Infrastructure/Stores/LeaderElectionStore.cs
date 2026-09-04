using JobScheduler.Application.Abstractions;
using JobScheduler.Domain.Entities;
using JobScheduler.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace JobScheduler.Infrastructure.Stores;

/// <summary>
/// Lease-based leader election over a single database row. Acquisition and renewal are conditional
/// <c>UPDATE</c>s so exactly one node can win. Acquiring (as opposed to renewing) bumps the fencing
/// token, so a paused old leader that resumes is fenced out — preventing split-brain.
/// </summary>
public sealed class LeaderElectionStore(AppDbContext db) : ILeaderElectionStore
{
    public async Task<LeaderView> TryAcquireOrRenewAsync(
        string nodeId, Guid token, DateTimeOffset now, TimeSpan ttl, CancellationToken ct)
    {
        await EnsureRowAsync(ct);
        var newExpiry = now + ttl;

        // 1) Fast path: we already hold it -> renew (no fencing bump).
        int renewed = await db.LeaderLeases
            .Where(l => l.Key == LeaderLease.SingletonKey && l.Owner == nodeId && l.Token == token && l.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.ExpiresAt, newExpiry)
                .SetProperty(l => l.Version, l => l.Version + 1), ct);

        if (renewed == 0)
        {
            // 2) Vacant or expired -> take over, bumping the fencing token to fence any old leader.
            await db.LeaderLeases
                .Where(l => l.Key == LeaderLease.SingletonKey && (l.Owner == null || l.ExpiresAt <= now))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(l => l.Owner, nodeId)
                    .SetProperty(l => l.Token, token)
                    .SetProperty(l => l.FencingToken, l => l.FencingToken + 1)
                    .SetProperty(l => l.AcquiredAt, now)
                    .SetProperty(l => l.ExpiresAt, newExpiry)
                    .SetProperty(l => l.Version, l => l.Version + 1), ct);
        }

        return await GetAsync(now, ct);
    }

    public async Task ReleaseAsync(string nodeId, Guid token, CancellationToken ct)
    {
        await db.LeaderLeases
            .Where(l => l.Key == LeaderLease.SingletonKey && l.Owner == nodeId && l.Token == token)
            .ExecuteUpdateAsync(s => s
                .SetProperty(l => l.Owner, (string?)null)
                .SetProperty(l => l.Token, Guid.Empty)
                .SetProperty(l => l.ExpiresAt, DateTimeOffset.MinValue)
                .SetProperty(l => l.Version, l => l.Version + 1), ct);
    }

    public async Task<LeaderView> GetAsync(DateTimeOffset now, CancellationToken ct)
    {
        var lease = await db.LeaderLeases.AsNoTracking()
            .FirstOrDefaultAsync(l => l.Key == LeaderLease.SingletonKey, ct);

        if (lease is null)
        {
            return new LeaderView(null, 0, null, DateTimeOffset.MinValue, false);
        }

        bool held = lease.Owner is not null && lease.ExpiresAt > now;
        return new LeaderView(held ? lease.Owner : null, lease.FencingToken, lease.AcquiredAt, lease.ExpiresAt, held);
    }

    private async Task EnsureRowAsync(CancellationToken ct)
    {
        if (await db.LeaderLeases.AnyAsync(l => l.Key == LeaderLease.SingletonKey, ct))
        {
            return;
        }

        try
        {
            await db.LeaderLeases.AddAsync(LeaderLease.CreateVacant(), ct);
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another node created the singleton row first; that is fine.
            db.ChangeTracker.Clear();
        }
    }
}
