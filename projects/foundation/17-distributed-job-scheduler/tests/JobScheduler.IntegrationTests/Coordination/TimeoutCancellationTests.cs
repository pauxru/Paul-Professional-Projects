using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.Infrastructure.Engine;
using JobScheduler.IntegrationTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace JobScheduler.IntegrationTests.Coordination;

/// <summary>
/// End-to-end execution proofs through the real <see cref="RunExecutor"/>: a hard per-run timeout
/// interrupts a slow handler, and a cooperative cancel request propagates into the executing
/// handler via the heartbeat channel. Both are bounded by a hard deadline so the suite terminates.
/// </summary>
public sealed class TimeoutCancellationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(30);

    private static async Task<(Guid Token, long Fencing)> ClaimAsync(
        SchedulerTestHost host, Guid runId, DateTimeOffset now, CancellationToken ct)
        => await host.InScopeAsync(async sp =>
        {
            var runs = sp.GetRequiredService<IJobRunStore>();
            var token = Guid.NewGuid();
            var claim = await runs.TryClaimAsync(runId, "node-A", token, now, Lease, singleton: false, ct);
            Assert.True(claim.Claimed);
            return (token, claim.FencingToken);
        });

    [Fact]
    public async Task Hard_timeout_interrupts_a_slow_handler_and_dead_letters_a_poison_run()
    {
        await using var host = new SchedulerTestHost(e =>
        {
            e.HeartbeatSeconds = 5;
            e.LeaseSeconds = 30;
        });
        await host.InitializeAsync();

        // 1s timeout vs a 30s handler; maxAttempts=1 so the single timeout is terminal (poison).
        var def = await host.AddDefinitionAsync(name: "slow-job", handler: "slow", maxAttempts: 1, timeoutSeconds: 1);
        var runId = await host.AddDueRunAsync(def);
        using var cts = new CancellationTokenSource(Deadline);

        var (token, fencing) = await ClaimAsync(host, runId, host.Clock.UtcNow, cts.Token);

        using (var scope = host.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<RunExecutor>();
            await executor.ExecuteAsync(runId, token, fencing, "node-A", CancellationToken.None).WaitAsync(cts.Token);
        }

        var run = await host.GetRunAsync(runId);
        Assert.Equal(RunState.DeadLettered, run!.State);

        var (dlqCount, firstError) = await host.InScopeAsync(async sp =>
        {
            var dlq = sp.GetRequiredService<IDeadLetterStore>();
            var page = await dlq.ListAsync(new PageRequest(1, 10), includeReplayed: true, default);
            return (page.TotalCount, page.Items.FirstOrDefault()?.Error);
        });
        Assert.Equal(1, dlqCount);
        Assert.Contains("timeout", firstError ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cooperative_cancel_request_propagates_into_the_running_handler()
    {
        await using var host = new SchedulerTestHost(e =>
        {
            e.HeartbeatSeconds = 1; // fast heartbeat so the cancel is observed quickly
            e.LeaseSeconds = 30;
        });
        await host.InitializeAsync();

        var def = await host.AddDefinitionAsync(name: "cancellable-slow", handler: "slow", timeoutSeconds: 300);
        var runId = await host.AddDueRunAsync(def);
        using var cts = new CancellationTokenSource(Deadline);

        var (token, fencing) = await ClaimAsync(host, runId, host.Clock.UtcNow, cts.Token);

        // Request cancellation before execution starts; the running handler must observe it.
        var cancelAccepted = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().RequestCancelAsync(runId, host.Clock.UtcNow, cts.Token));
        Assert.True(cancelAccepted);

        using (var scope = host.CreateScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<RunExecutor>();
            await executor.ExecuteAsync(runId, token, fencing, "node-A", CancellationToken.None).WaitAsync(cts.Token);
        }

        var run = await host.GetRunAsync(runId);
        Assert.Equal(RunState.Cancelled, run!.State);
    }

    [Fact]
    public async Task RequestCancel_on_a_pending_run_cancels_it_immediately()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        var def = await host.AddDefinitionAsync();
        var runId = await host.AddDueRunAsync(def);
        using var cts = new CancellationTokenSource(Deadline);

        var ok = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().RequestCancelAsync(runId, host.Clock.UtcNow, cts.Token));
        Assert.True(ok);

        var run = await host.GetRunAsync(runId);
        Assert.Equal(RunState.Cancelled, run!.State);
    }
}
