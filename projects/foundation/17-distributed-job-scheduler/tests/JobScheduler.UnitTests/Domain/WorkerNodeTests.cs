using JobScheduler.Domain;
using JobScheduler.Domain.Entities;

namespace JobScheduler.UnitTests.Domain;

/// <summary>Worker registry semantics: tag/capability matching, liveness and draining.</summary>
public sealed class WorkerNodeTests
{
    private static readonly DateTimeOffset T0 = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static WorkerNode Node(params string[] tags) =>
        WorkerNode.Register("node-1", "host", tags, maxConcurrency: 4, T0);

    [Fact]
    public void No_required_tags_runs_anywhere()
    {
        Assert.True(Node().CanRun([]));
        Assert.True(Node("gpu").CanRun([]));
    }

    [Fact]
    public void Required_tags_must_all_be_advertised()
    {
        var node = Node("gpu", "us-east");
        Assert.True(node.CanRun(["gpu"]));
        Assert.True(node.CanRun(["gpu", "us-east"]));
        Assert.False(node.CanRun(["gpu", "eu-west"])); // missing a required tag
    }

    [Fact]
    public void Tag_matching_is_case_insensitive()
    {
        Assert.True(Node("GPU").CanRun(["gpu"]));
    }

    [Fact]
    public void IsAlive_respects_the_heartbeat_ttl()
    {
        var node = Node();
        var ttl = TimeSpan.FromSeconds(30);
        Assert.True(node.IsAlive(T0.AddSeconds(10), ttl));
        Assert.False(node.IsAlive(T0.AddSeconds(31), ttl));
    }

    [Fact]
    public void Heartbeat_revives_a_node_marked_dead()
    {
        var node = Node();
        node.MarkDead();
        Assert.Equal(WorkerStatus.Dead, node.Status);

        node.Heartbeat(T0.AddSeconds(5));
        Assert.Equal(WorkerStatus.Active, node.Status);
    }

    [Fact]
    public void Draining_node_stops_being_active()
    {
        var node = Node();
        node.BeginDraining(T0.AddSeconds(1));
        Assert.Equal(WorkerStatus.Draining, node.Status);
    }

    [Fact]
    public void Dead_node_is_never_alive_even_within_ttl()
    {
        var node = Node();
        node.MarkDead();
        Assert.False(node.IsAlive(T0, TimeSpan.FromHours(1)));
    }
}
