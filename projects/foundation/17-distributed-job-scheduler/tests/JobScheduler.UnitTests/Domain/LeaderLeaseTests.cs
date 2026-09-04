using JobScheduler.Domain.Entities;

namespace JobScheduler.UnitTests.Domain;

/// <summary>Leader lease acquire/renew/release with fencing-token bumps that prevent split-brain.</summary>
public sealed class LeaderLeaseTests
{
    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(15);

    [Fact]
    public void Vacant_lease_is_not_held()
    {
        var lease = LeaderLease.CreateVacant();
        Assert.False(lease.IsHeld(T0));
    }

    [Fact]
    public void Acquire_takes_ownership_and_bumps_fencing_to_one()
    {
        var lease = LeaderLease.CreateVacant();
        var token = Guid.NewGuid();
        lease.Acquire("node-1", token, T0, Ttl);

        Assert.True(lease.IsHeldBy("node-1", T0.AddSeconds(1)));
        Assert.Equal(1, lease.FencingToken);
    }

    [Fact]
    public void Renew_extends_only_for_the_matching_owner_and_token()
    {
        var lease = LeaderLease.CreateVacant();
        var token = Guid.NewGuid();
        lease.Acquire("node-1", token, T0, Ttl);

        Assert.False(lease.Renew("node-1", Guid.NewGuid(), T0.AddSeconds(5), Ttl)); // wrong token
        Assert.True(lease.Renew("node-1", token, T0.AddSeconds(5), Ttl));
        Assert.True(lease.IsHeld(T0.AddSeconds(15)));
    }

    [Fact]
    public void Acquiring_a_held_lease_from_another_node_throws()
    {
        var lease = LeaderLease.CreateVacant();
        lease.Acquire("node-1", Guid.NewGuid(), T0, Ttl);

        Assert.Throws<InvalidOperationException>(
            () => lease.Acquire("node-2", Guid.NewGuid(), T0.AddSeconds(1), Ttl));
    }

    [Fact]
    public void Takeover_after_expiry_bumps_fencing_and_fences_the_old_leader()
    {
        var lease = LeaderLease.CreateVacant();
        lease.Acquire("node-1", Guid.NewGuid(), T0, Ttl);
        Assert.Equal(1, lease.FencingToken);

        // node-1's lease has expired; node-2 takes over.
        var afterExpiry = T0 + Ttl + TimeSpan.FromSeconds(1);
        Assert.False(lease.IsHeld(afterExpiry));
        lease.Acquire("node-2", Guid.NewGuid(), afterExpiry, Ttl);

        Assert.True(lease.IsHeldBy("node-2", afterExpiry.AddSeconds(1)));
        Assert.Equal(2, lease.FencingToken); // strictly greater -> old leader is fenced
    }

    [Fact]
    public void Release_clears_ownership()
    {
        var lease = LeaderLease.CreateVacant();
        var token = Guid.NewGuid();
        lease.Acquire("node-1", token, T0, Ttl);
        lease.Release("node-1", token);
        Assert.False(lease.IsHeld(T0.AddSeconds(1)));
    }
}
