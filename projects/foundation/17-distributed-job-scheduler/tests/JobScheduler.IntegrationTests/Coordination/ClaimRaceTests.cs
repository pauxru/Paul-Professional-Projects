using JobScheduler.Application.Abstractions;
using JobScheduler.Application.Services;
using JobScheduler.Domain;
using JobScheduler.IntegrationTests.TestSupport;
using Microsoft.Extensions.DependencyInjection;

namespace JobScheduler.IntegrationTests.Coordination;

/// <summary>
/// The headline correctness proofs, run against a real file-backed SQLite database so multiple
/// worker connections genuinely contend on the claim UPDATE. Every test uses a hard
/// <see cref="CancellationTokenSource"/> deadline so the suite always terminates.
/// </summary>
public sealed class ClaimRaceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task N_workers_racing_for_one_due_job_produce_exactly_one_winner()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        var def = await host.AddDefinitionAsync();
        var runId = await host.AddDueRunAsync(def);
        var now = host.Clock.UtcNow;

        const int workers = 16;
        using var cts = new CancellationTokenSource(Deadline);
        using var startBarrier = new Barrier(workers);

        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
        {
            // Release all workers at the same instant to maximise contention on the claim.
            startBarrier.SignalAndWait(cts.Token);
            using var scope = host.CreateScope();
            var runs = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
            var result = await runs.TryClaimAsync(
                runId, $"node-{i}", Guid.NewGuid(), now, TimeSpan.FromSeconds(30), singleton: false, cts.Token);
            return result.Claimed;
        }, cts.Token)).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(won => won)); // exactly one winner

        var run = await host.GetRunAsync(runId);
        Assert.NotNull(run);
        Assert.Equal(RunState.Claimed, run!.State);
        Assert.Equal(1, run.FencingToken);       // claim minted exactly one fencing token
        Assert.NotNull(run.LeaseOwner);
    }

    [Fact]
    public async Task Singleton_definition_never_has_two_active_instances()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        var def = await host.AddDefinitionAsync(name: "singleton-job", singleton: true);

        // Three distinct due runs of the SAME singleton definition.
        var runIds = new List<Guid>();
        for (int i = 0; i < 3; i++)
        {
            runIds.Add(await host.AddDueRunAsync(def));
        }
        var now = host.Clock.UtcNow;

        using var cts = new CancellationTokenSource(Deadline);
        using var startBarrier = new Barrier(runIds.Count);

        var tasks = runIds.Select((id, i) => Task.Run(async () =>
        {
            startBarrier.SignalAndWait(cts.Token);
            using var scope = host.CreateScope();
            var runs = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
            var result = await runs.TryClaimAsync(
                id, $"node-{i}", Guid.NewGuid(), now, TimeSpan.FromSeconds(30), singleton: true, cts.Token);
            return result.Claimed;
        }, cts.Token)).ToArray();

        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(won => won)); // singleton guard admits exactly one

        var active = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().CountActiveByDefinitionAsync(def.Id, default));
        Assert.Equal(1, active);
    }

    [Fact]
    public async Task Per_definition_concurrency_cap_is_respected_under_load()
    {
        await using var host = new SchedulerTestHost();
        await host.InitializeAsync();
        var def = await host.AddDefinitionAsync(name: "capped-job", concurrencyLimit: 2);

        var runIds = new List<Guid>();
        for (int i = 0; i < 5; i++)
        {
            runIds.Add(await host.AddDueRunAsync(def));
        }
        var now = host.Clock.UtcNow;

        using var cts = new CancellationTokenSource(Deadline);

        // Simulate a worker admission loop: consult the gate against live counts before claiming.
        int started = 0;
        foreach (var id in runIds)
        {
            await host.InScopeAsync(async sp =>
            {
                var runs = sp.GetRequiredService<IJobRunStore>();
                int globalActive = await runs.CountActiveGlobalAsync(cts.Token);
                int defActive = await runs.CountActiveByDefinitionAsync(def.Id, cts.Token);
                bool anyActive = await runs.HasActiveForDefinitionAsync(def.Id, cts.Token);

                if (!ConcurrencyGate.CanStart(globalActive, host.Engine.GlobalMaxConcurrency,
                        defActive, def.ConcurrencyLimit, 0, 0, def.Singleton, anyActive))
                {
                    return;
                }

                var claim = await runs.TryClaimAsync(id, "node-1", Guid.NewGuid(), now, TimeSpan.FromSeconds(30), false, cts.Token);
                if (claim.Claimed)
                {
                    await runs.TryStartAsync(id, claim.Run!.LeaseToken!.Value, now, cts.Token);
                    started++;
                }
            });
        }

        Assert.Equal(2, started); // never exceeded the per-definition cap of 2

        var active = await host.InScopeAsync(sp =>
            sp.GetRequiredService<IJobRunStore>().CountActiveByDefinitionAsync(def.Id, default));
        Assert.Equal(2, active);
    }
}
