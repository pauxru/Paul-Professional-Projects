using JobScheduler.Application.Abstractions;
using JobScheduler.IntegrationTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace JobScheduler.IntegrationTests.Coordination;

/// <summary>
/// Lease-based leader election: at most one leader at any instant, automatic failover once the
/// dead leader's lease expires, and a fencing-token bump on every take-over so a revived old leader
/// is fenced out (split-brain avoidance).
/// </summary>
public sealed class LeaderElectionTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task Concurrent_campaigners_yield_exactly_one_leader()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        var now = host.Clock.UtcNow;

        const int nodes = 12;
        using var cts = new CancellationTokenSource(Deadline);
        using var barrier = new Barrier(nodes);

        var tasks = Enumerable.Range(0, nodes).Select(i => Task.Run(async () =>
        {
            barrier.SignalAndWait(cts.Token);
            using var scope = host.CreateScope();
            var election = scope.ServiceProvider.GetRequiredService<ILeaderElectionStore>();
            var view = await election.TryAcquireOrRenewAsync($"node-{i}", Guid.NewGuid(), now, Ttl, cts.Token);
            return view.Owner;
        }, cts.Token)).ToArray();

        var owners = await Task.WhenAll(tasks);

        // All observers converge on a single owner.
        var distinct = owners.Where(o => o is not null).Distinct().ToList();
        Assert.Single(distinct);

        var final = await host.InScopeAsync(sp =>
            sp.GetRequiredService<ILeaderElectionStore>().GetAsync(now.AddSeconds(1), default));
        Assert.True(final.IsHeld);
        Assert.Equal(distinct[0], final.Owner);
    }

    [Fact]
    public async Task A_second_node_cannot_steal_a_lease_that_is_still_held()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        var now = host.Clock.UtcNow;
        using var cts = new CancellationTokenSource(Deadline);

        await host.InScopeAsync(sp =>
            sp.GetRequiredService<ILeaderElectionStore>().TryAcquireOrRenewAsync("node-1", Guid.NewGuid(), now, Ttl, cts.Token));

        // node-2 campaigns while node-1's lease is still valid -> denied.
        var view = await host.InScopeAsync(sp =>
            sp.GetRequiredService<ILeaderElectionStore>().TryAcquireOrRenewAsync("node-2", Guid.NewGuid(), now.AddSeconds(5), Ttl, cts.Token));

        Assert.Equal("node-1", view.Owner);
    }

    [Fact]
    public async Task Leadership_fails_over_after_the_ttl_and_bumps_the_fencing_token()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);

        var t0 = host.Clock.UtcNow;
        var first = await host.InScopeAsync(sp =>
            sp.GetRequiredService<ILeaderElectionStore>().TryAcquireOrRenewAsync("node-1", Guid.NewGuid(), t0, Ttl, cts.Token));
        Assert.Equal("node-1", first.Owner);
        Assert.Equal(1, first.FencingToken);

        // node-1 dies (stops renewing); its lease expires.
        host.Clock.Advance(Ttl + TimeSpan.FromSeconds(1));
        var t1 = host.Clock.UtcNow;

        var second = await host.InScopeAsync(sp =>
            sp.GetRequiredService<ILeaderElectionStore>().TryAcquireOrRenewAsync("node-2", Guid.NewGuid(), t1, Ttl, cts.Token));

        Assert.True(second.IsHeld);
        Assert.Equal("node-2", second.Owner);
        Assert.Equal(2, second.FencingToken); // strictly greater -> old leader fenced out
    }

    [Fact]
    public async Task The_holder_renews_without_bumping_the_fencing_token()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);
        var token = Guid.NewGuid();

        var t0 = host.Clock.UtcNow;
        var first = await host.InScopeAsync(sp =>
            sp.GetRequiredService<ILeaderElectionStore>().TryAcquireOrRenewAsync("node-1", token, t0, Ttl, cts.Token));
        Assert.Equal(1, first.FencingToken);

        // Same node renews within the TTL using its own token.
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        var renewed = await host.InScopeAsync(sp =>
            sp.GetRequiredService<ILeaderElectionStore>().TryAcquireOrRenewAsync("node-1", token, host.Clock.UtcNow, Ttl, cts.Token));

        Assert.Equal("node-1", renewed.Owner);
        Assert.Equal(1, renewed.FencingToken); // renew must NOT bump the token
    }
}
