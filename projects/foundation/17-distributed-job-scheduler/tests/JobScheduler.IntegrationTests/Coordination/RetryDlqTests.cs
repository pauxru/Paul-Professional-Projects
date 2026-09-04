using JobScheduler.Application.Abstractions;
using JobScheduler.Domain;
using JobScheduler.IntegrationTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace JobScheduler.IntegrationTests.Coordination;

/// <summary>
/// Retry/dead-letter lifecycle driven through the real <c>RunExecutor</c> and the deterministic
/// flaky handler: transient failures within budget eventually succeed; exhausting the budget parks
/// the run in the DLQ; replaying the DLQ entry re-arms the original run as a fresh Pending instance.
/// </summary>
public sealed class RetryDlqTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Transient_failures_within_budget_eventually_succeed()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);

        // Fails on attempt 1, succeeds on attempt 2; budget of 3 allows it.
        var def = await host.AddDefinitionAsync(name: "flaky-recovers", handler: "flaky", maxAttempts: 3, payloadJson: "{\"failTimes\":1}");
        var runId = await host.AddDueRunAsync(def);

        await host.ClaimAndExecuteAsync(runId, ct: cts.Token);   // attempt 1 -> Failed -> retry scheduled
        var afterFirst = await host.GetRunAsync(runId);
        Assert.Equal(RunState.Pending, afterFirst!.State); // re-armed for retry

        host.Clock.Advance(TimeSpan.FromMinutes(10));            // jump past the backoff delay
        await host.ClaimAndExecuteAsync(runId, ct: cts.Token);   // attempt 2 -> Succeeded

        var run = await host.GetRunAsync(runId);
        Assert.Equal(RunState.Succeeded, run!.State);
        Assert.Equal(2, run.AttemptCount);
    }

    [Fact]
    public async Task Exhausting_the_attempt_budget_dead_letters_then_replay_rearms_the_run()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        using var cts = new CancellationTokenSource(Deadline);

        // Always fails; budget of 2 -> dead-letter after the second attempt.
        var def = await host.AddDefinitionAsync(name: "always-fails", handler: "flaky", maxAttempts: 2, payloadJson: "{\"failTimes\":9}");
        var runId = await host.AddDueRunAsync(def);

        await host.ClaimAndExecuteAsync(runId, ct: cts.Token);   // attempt 1 -> retry
        host.Clock.Advance(TimeSpan.FromMinutes(10));
        await host.ClaimAndExecuteAsync(runId, ct: cts.Token);   // attempt 2 -> dead-letter

        var dead = await host.GetRunAsync(runId);
        Assert.Equal(RunState.DeadLettered, dead!.State);

        var dlqBefore = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IDeadLetterStore>().CountAsync(includeReplayed: false, cts.Token));
        Assert.Equal(1, dlqBefore);

        // Replay mirrors the DLQ endpoint: re-arm the run and mark the entry replayed.
        await host.InScopeAsync(async sp =>
        {
            var dlq = sp.GetRequiredService<IDeadLetterStore>();
            var runs = sp.GetRequiredService<IJobRunStore>();
            var page = await dlq.ListAsync(new PageRequest(1, 10), includeReplayed: false, cts.Token);
            var entryId = page.Items.Single().Id;

            var entry = await dlq.GetAsync(entryId, cts.Token);
            var run = await runs.ReplayResetAsync(entry!.JobRunId, host.Clock.UtcNow, cts.Token);
            Assert.NotNull(run);
            entry.MarkReplayed(run!.Id, host.Clock.UtcNow);
            await dlq.SaveChangesAsync(cts.Token);
        });

        var replayed = await host.GetRunAsync(runId);
        Assert.Equal(RunState.Pending, replayed!.State);
        Assert.Equal(0, replayed.AttemptCount); // fresh instance

        var dlqActive = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IDeadLetterStore>().CountAsync(includeReplayed: false, cts.Token));
        Assert.Equal(0, dlqActive); // entry no longer counts as active
    }
}
