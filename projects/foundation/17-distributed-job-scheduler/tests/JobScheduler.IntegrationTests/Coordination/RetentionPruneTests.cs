using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.IntegrationTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace JobScheduler.IntegrationTests.Coordination;

/// <summary>
/// Retention pruning driven by the <see cref="FakeClock"/>: finished runs (and their logs) older
/// than the cutoff are deleted, while still-active runs and recently finished runs are preserved.
/// </summary>
public sealed class RetentionPruneTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Prune_deletes_runs_and_logs_finished_before_the_cutoff()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);

        var def = await host.AddDefinitionAsync(name: "retained", handler: "report-generator");
        var runId = await host.AddDueRunAsync(def);
        await host.ClaimAndExecuteAsync(runId, ct: cts.Token);

        var finished = await host.GetRunAsync(runId);
        Assert.Equal(RunState.Succeeded, finished!.State);

        // Runs finished long ago fall outside the retention window.
        host.Clock.Advance(TimeSpan.FromDays(30));
        var cutoff = host.Clock.UtcNow;

        var (prunedRuns, prunedLogs) = await host.InScopeAsync(async sp =>
        {
            int runs = await sp.GetRequiredService<IJobRunStore>().PruneAsync(cutoff, cts.Token);
            int logs = await sp.GetRequiredService<IRunLogStore>().PruneAsync(cutoff, cts.Token);
            return (runs, logs);
        });

        Assert.Equal(1, prunedRuns);
        Assert.True(prunedLogs >= 1); // execution appended at least one structured log
        Assert.Null(await host.GetRunAsync(runId));
    }

    [Fact]
    public async Task Prune_keeps_recent_finished_runs_and_active_runs()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);

        var def = await host.AddDefinitionAsync(name: "fresh", handler: "report-generator");

        // A freshly succeeded run (finished "now").
        var freshId = await host.AddDueRunAsync(def);
        await host.ClaimAndExecuteAsync(freshId, ct: cts.Token);

        // A pending run that has never finished.
        var pendingId = await host.AddDueRunAsync(def);

        // Cutoff is one hour before now: nothing finished that long ago.
        var cutoff = host.Clock.UtcNow.AddHours(-1);
        var pruned = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().PruneAsync(cutoff, cts.Token));

        Assert.Equal(0, pruned);
        Assert.Equal(RunState.Succeeded, (await host.GetRunAsync(freshId))!.State);
        Assert.Equal(RunState.Pending, (await host.GetRunAsync(pendingId))!.State);
    }
}
