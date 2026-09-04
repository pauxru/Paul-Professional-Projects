using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.Domain.Entities;
using JobScheduler.IntegrationTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace JobScheduler.IntegrationTests.Coordination;

/// <summary>
/// Worker-node registry: registration + heartbeat keep a node alive, draining stops it claiming,
/// a stale heartbeat is reaped as dead once past the TTL, and capability tags gate which node a job
/// can land on.
/// </summary>
public sealed class WorkerRegistryTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    private static Task<T> WithRegistry<T>(SchedulerTestHost host, Func<IWorkerRegistry, Task<T>> work)
        => host.InScopeAsync(sp => work(sp.GetRequiredService<IWorkerRegistry>()));

    [Fact]
    public async Task Register_then_heartbeat_keeps_the_node_alive()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);

        await WithRegistry(host, async r =>
        {
            await r.RegisterAsync("w1", "host-1", ["gpu"], 4, host.Clock.UtcNow, cts.Token);
            return 0;
        });

        host.Clock.Advance(TimeSpan.FromSeconds(20));
        var beat = await WithRegistry(host, r => r.HeartbeatAsync("w1", host.Clock.UtcNow, cts.Token));
        Assert.True(beat);

        var node = await WithRegistry(host, r => r.GetAsync("w1", cts.Token));
        Assert.NotNull(node);
        Assert.Equal(WorkerStatus.Active, node!.Status);
        Assert.True(node.IsAlive(host.Clock.UtcNow, Ttl));
    }

    [Fact]
    public async Task A_node_with_a_stale_heartbeat_is_reaped_as_dead()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);

        await WithRegistry(host, async r =>
        {
            await r.RegisterAsync("w-live", "host-a", [], 2, host.Clock.UtcNow, cts.Token);
            await r.RegisterAsync("w-dead", "host-b", [], 2, host.Clock.UtcNow, cts.Token);
            return 0;
        });

        // Advance beyond the TTL, but keep only the live node beating.
        host.Clock.Advance(Ttl + TimeSpan.FromSeconds(5));
        await WithRegistry(host, r => r.HeartbeatAsync("w-live", host.Clock.UtcNow, cts.Token));

        var reaped = await WithRegistry(host, r => r.ReapDeadNodesAsync(host.Clock.UtcNow, Ttl, cts.Token));
        Assert.Equal(1, reaped);

        Assert.Equal(WorkerStatus.Active, (await WithRegistry(host, r => r.GetAsync("w-live", cts.Token)))!.Status);
        Assert.Equal(WorkerStatus.Dead, (await WithRegistry(host, r => r.GetAsync("w-dead", cts.Token)))!.Status);
    }

    [Fact]
    public async Task Draining_marks_the_node_so_it_stops_claiming()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);

        await WithRegistry(host, async r =>
        {
            await r.RegisterAsync("w1", "host-1", [], 4, host.Clock.UtcNow, cts.Token);
            await r.BeginDrainAsync("w1", host.Clock.UtcNow, cts.Token);
            return 0;
        });

        var node = await WithRegistry(host, r => r.GetAsync("w1", cts.Token));
        Assert.Equal(WorkerStatus.Draining, node!.Status);

        // A heartbeat while draining must not flip the node back to Active.
        host.Clock.Advance(TimeSpan.FromSeconds(5));
        await WithRegistry(host, r => r.HeartbeatAsync("w1", host.Clock.UtcNow, cts.Token));
        var afterBeat = await WithRegistry(host, r => r.GetAsync("w1", cts.Token));
        Assert.Equal(WorkerStatus.Draining, afterBeat!.Status);
    }

    [Fact]
    public async Task Capability_tags_gate_which_node_can_run_a_job()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);

        await WithRegistry(host, async r =>
        {
            await r.RegisterAsync("gpu-node", "host-gpu", ["gpu", "linux"], 4, host.Clock.UtcNow, cts.Token);
            await r.RegisterAsync("cpu-node", "host-cpu", ["linux"], 4, host.Clock.UtcNow, cts.Token);
            return 0;
        });

        var gpu = await WithRegistry(host, r => r.GetAsync("gpu-node", cts.Token));
        var cpu = await WithRegistry(host, r => r.GetAsync("cpu-node", cts.Token));

        string[] requiresGpu = ["gpu"];
        Assert.True(gpu!.CanRun(requiresGpu));
        Assert.False(cpu!.CanRun(requiresGpu));
        Assert.True(cpu.CanRun([])); // an untagged job runs anywhere
    }
}
