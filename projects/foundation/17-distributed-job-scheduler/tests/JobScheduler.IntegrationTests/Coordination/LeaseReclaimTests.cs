using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.IntegrationTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace JobScheduler.IntegrationTests.Coordination;

/// <summary>
/// Lease lifecycle proofs: heartbeat keeps ownership, an expired lease is reclaimed for another
/// node, and a stalled worker's late write-back is rejected by the fencing token so state is never
/// corrupted (the core split-brain-avoidance guarantee).
/// </summary>
public sealed class LeaseReclaimTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private static async Task<(Guid Token, long Fencing)> ClaimAndStartAsync(
        SchedulerTestHost host, Guid runId, string node, DateTimeOffset now, CancellationToken ct)
        => await host.InScopeAsync(async sp =>
        {
            var runs = sp.GetRequiredService<IJobRunStore>();
            var token = Guid.NewGuid();
            var claim = await runs.TryClaimAsync(runId, node, token, now, Lease, singleton: false, ct);
            Assert.True(claim.Claimed);
            Assert.True(await runs.TryStartAsync(runId, token, now, ct));
            return (token, claim.FencingToken);
        });

    [Fact]
    public async Task Heartbeat_extends_the_lease_for_a_healthy_worker()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        var def = await host.AddDefinitionAsync();
        var runId = await host.AddDueRunAsync(def);
        var now = host.Clock.UtcNow;
        using var cts = new CancellationTokenSource(Deadline);

        var (token, _) = await ClaimAndStartAsync(host, runId, "A", now, cts.Token);

        var newExpiry = now.AddSeconds(60);
        var hb = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().HeartbeatAsync(runId, token, newExpiry, cts.Token));
        Assert.True(hb.LeaseHeld);

        var run = await host.GetRunAsync(runId);
        Assert.Equal(newExpiry, run!.LeaseExpiresAt);

        // A heartbeat presenting the wrong token does not own the lease.
        var wrong = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().HeartbeatAsync(runId, Guid.NewGuid(), now.AddSeconds(120), cts.Token));
        Assert.False(wrong.LeaseHeld);
    }

    [Fact]
    public async Task Expired_lease_is_reclaimed_and_the_stalled_workers_write_is_fenced_out()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        var def = await host.AddDefinitionAsync();
        var runId = await host.AddDueRunAsync(def);
        using var cts = new CancellationTokenSource(Deadline);

        // Worker A claims and starts (fencing token 1).
        var (_, aFencing) = await ClaimAndStartAsync(host, runId, "A", host.Clock.UtcNow, cts.Token);
        Assert.Equal(1, aFencing);

        // A stalls; time advances beyond the lease. The reaper reclaims the expired lease.
        host.Clock.Advance(TimeSpan.FromSeconds(31));
        var reclaimed = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().ReclaimExpiredLeasesAsync(host.Clock.UtcNow, cts.Token));
        Assert.Equal(1, reclaimed);
        Assert.Equal(RunState.Pending, (await host.GetRunAsync(runId))!.State);

        // Worker B re-claims (fencing bumps to 2) and starts.
        var (_, bFencing) = await ClaimAndStartAsync(host, runId, "B", host.Clock.UtcNow, cts.Token);
        Assert.Equal(2, bFencing);

        // Worker A finally returns and tries to write its result with the STALE token -> rejected.
        var aWrote = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().TryCompleteAsync(
                runId, aFencing, RunState.Succeeded, "A-output", null, host.Clock.UtcNow, cts.Token));
        Assert.False(aWrote);

        // Worker B completes normally with the current token.
        var bWrote = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().TryCompleteAsync(
                runId, bFencing, RunState.Succeeded, "B-output", null, host.Clock.UtcNow, cts.Token));
        Assert.True(bWrote);

        var final = await host.GetRunAsync(runId);
        Assert.Equal(RunState.Succeeded, final!.State);
        Assert.Equal("B-output", final.Output); // A's stale write never corrupted the result
    }

    [Fact]
    public async Task Reaper_does_not_touch_a_lease_that_is_still_valid()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        var def = await host.AddDefinitionAsync();
        var runId = await host.AddDueRunAsync(def);
        using var cts = new CancellationTokenSource(Deadline);

        await ClaimAndStartAsync(host, runId, "A", host.Clock.UtcNow, cts.Token);

        // Only 10s pass on a 30s lease -> nothing to reclaim.
        host.Clock.Advance(TimeSpan.FromSeconds(10));
        var reclaimed = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().ReclaimExpiredLeasesAsync(host.Clock.UtcNow, cts.Token));
        Assert.Equal(0, reclaimed);
        Assert.Equal(RunState.Running, (await host.GetRunAsync(runId))!.State);
    }
}
