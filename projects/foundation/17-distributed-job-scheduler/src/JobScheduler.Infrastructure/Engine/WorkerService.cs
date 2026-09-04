using System.Diagnostics;
using JobScheduler.Application.Abstractions;
using JobScheduler.Application.Options;
using JobScheduler.Application.Services;
using JobScheduler.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScheduler.Infrastructure.Engine;

/// <summary>
/// The worker loop. Registers this node, heartbeats it, and repeatedly polls for due runs it is
/// eligible to run (tag match, circuit closed, concurrency slots free), claims them atomically, and
/// executes each via <see cref="RunExecutor"/>. On shutdown it drains: it stops claiming new work
/// and lets in-flight runs finish within a grace window.
/// </summary>
public sealed class WorkerService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<EngineOptions> engineOptions,
    IOptions<NodeOptions> nodeOptions,
    ISchedulerMetrics metrics,
    ILogger<WorkerService> logger) : BackgroundService
{
    private readonly EngineOptions _engine = engineOptions.Value;
    private readonly NodeOptions _node = nodeOptions.Value;
    private readonly SemaphoreSlim _slots = new(Math.Max(1, nodeOptions.Value.MaxConcurrency));
    private readonly List<Task> _inFlight = [];
    private volatile bool _draining;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_node.RunWorker)
        {
            return;
        }

        await RegisterAsync(stoppingToken);
        logger.LogInformation("Worker {NodeId} started (maxConcurrency={Max}, tags=[{Tags}]).",
            _node.NodeId, _node.MaxConcurrency, string.Join(",", _node.Tags));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await HeartbeatNodeAsync(stoppingToken);
                if (!_draining)
                {
                    await ClaimAndDispatchAsync(stoppingToken);
                }
                PruneCompleted();
                await Task.Delay(_engine.Poll, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker {NodeId} poll loop error.", _node.NodeId);
                try { await Task.Delay(_engine.Poll, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }

        await DrainAsync();
    }

    private async Task ClaimAndDispatchAsync(CancellationToken stoppingToken)
    {
        int freeSlots = _slots.CurrentCount;
        if (freeSlots == 0)
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var runs = sp.GetRequiredService<IJobRunStore>();
        var definitions = sp.GetRequiredService<IJobDefinitionStore>();
        var circuit = sp.GetRequiredService<IJobCircuitBreaker>();

        var now = clock.UtcNow;
        var due = await runs.GetDueAsync(now, _engine.PriorityAgingPerMinute, Math.Max(freeSlots * 2, freeSlots), stoppingToken);
        metrics.SetQueueDepth(await runs.CountByStateAsync(Domain.RunState.Pending, stoppingToken));

        foreach (var candidate in due)
        {
            if (_slots.CurrentCount == 0 || stoppingToken.IsCancellationRequested)
            {
                break;
            }

            var def = await definitions.GetAsync(candidate.JobDefinitionId, stoppingToken);
            if (def is null)
            {
                continue;
            }

            // Capability/tag routing: only run jobs whose required tags this node advertises.
            if (!NodeCanRun(def.Tags))
            {
                continue;
            }

            // Circuit breaker: skip a job type whose circuit is open.
            if (circuit.IsOpen(def.Id, now))
            {
                continue;
            }

            // Concurrency admission control.
            int globalActive = await runs.CountActiveGlobalAsync(stoppingToken);
            int defActive = await runs.CountActiveByDefinitionAsync(def.Id, stoppingToken);
            int queueActive = await runs.CountActiveByQueueAsync(def.Queue, stoppingToken);
            int queueSlots = _engine.QueueSlots.GetValueOrDefault(def.Queue, 0);
            bool anyActive = await runs.HasActiveForDefinitionAsync(def.Id, stoppingToken);

            if (!ConcurrencyGate.CanStart(globalActive, _engine.GlobalMaxConcurrency, defActive, def.ConcurrencyLimit,
                    queueActive, queueSlots, def.Singleton, anyActive))
            {
                continue;
            }

            if (!_slots.Wait(0))
            {
                break;
            }

            var claimStarted = Stopwatch.GetTimestamp();
            var leaseToken = Guid.NewGuid();
            var claim = await runs.TryClaimAsync(candidate.Id, _node.NodeId, leaseToken, now, _engine.Lease, def.Singleton, stoppingToken);
            if (!claim.Claimed || claim.Run is null)
            {
                _slots.Release();
                continue;
            }

            metrics.RunClaimed();
            metrics.RecordClaimLatency(Stopwatch.GetElapsedTime(claimStarted).TotalMilliseconds);
            long fencing = claim.FencingToken;
            var runId = candidate.Id;

            var task = Task.Run(() => ExecuteTrackedAsync(runId, leaseToken, fencing, stoppingToken), CancellationToken.None);
            lock (_inFlight)
            {
                _inFlight.Add(task);
            }
        }
    }

    private async Task ExecuteTrackedAsync(Guid runId, Guid leaseToken, long fencing, CancellationToken hardStop)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var executor = scope.ServiceProvider.GetRequiredService<RunExecutor>();
            await executor.ExecuteAsync(runId, leaseToken, fencing, _node.NodeId, hardStop);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled error executing run {RunId} on {NodeId}.", runId, _node.NodeId);
        }
        finally
        {
            _slots.Release();
        }
    }

    private bool NodeCanRun(IReadOnlyList<string> requiredTags)
    {
        if (requiredTags.Count == 0)
        {
            return true;
        }
        var mine = new HashSet<string>(_node.Tags, StringComparer.OrdinalIgnoreCase);
        return requiredTags.All(mine.Contains);
    }

    private void PruneCompleted()
    {
        lock (_inFlight)
        {
            _inFlight.RemoveAll(t => t.IsCompleted);
        }
    }

    /// <summary>Public entry so a host can request a graceful drain before stopping.</summary>
    public void BeginDrain() => _draining = true;

    private async Task DrainAsync()
    {
        _draining = true;
        logger.LogInformation("Worker {NodeId} draining {Count} in-flight run(s).", _node.NodeId, _inFlight.Count);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var registry = scope.ServiceProvider.GetRequiredService<IWorkerRegistry>();
            await registry.BeginDrainAsync(_node.NodeId, clock.UtcNow, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Worker {NodeId} failed to mark draining.", _node.NodeId);
        }

        Task[] pending;
        lock (_inFlight)
        {
            pending = _inFlight.ToArray();
        }

        try
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(_engine.LeaseSeconds), CancellationToken.None);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("Worker {NodeId} drain grace elapsed; remaining runs will be reclaimed.", _node.NodeId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Worker {NodeId} drain completed with errors.", _node.NodeId);
        }
    }

    private async Task RegisterAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IWorkerRegistry>();
        await registry.RegisterAsync(_node.NodeId, Environment.MachineName, _node.Tags, _node.MaxConcurrency, clock.UtcNow, ct);
    }

    private async Task HeartbeatNodeAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var registry = scope.ServiceProvider.GetRequiredService<IWorkerRegistry>();
        await registry.HeartbeatAsync(_node.NodeId, clock.UtcNow, ct);
    }
}
