using JobScheduler.Application.Abstractions;
using JobScheduler.Application.Options;
using JobScheduler.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScheduler.Infrastructure.Engine;

/// <summary>
/// Campaigns for the single leadership lease and, while it holds leadership, performs the singleton
/// duties that must not run concurrently across the fleet: materialising schedules into runs,
/// reaping expired leases and dead nodes, and enforcing retention. Leadership is fenced: a revived
/// old leader whose lease expired cannot resume because a new leader has a higher fencing token.
/// </summary>
public sealed class LeaderService(
    IServiceScopeFactory scopeFactory,
    IClock clock,
    IOptions<EngineOptions> engineOptions,
    IOptions<NodeOptions> nodeOptions,
    ISchedulerMetrics metrics,
    ILogger<LeaderService> logger) : BackgroundService
{
    private readonly EngineOptions _engine = engineOptions.Value;
    private readonly NodeOptions _node = nodeOptions.Value;
    private readonly Guid _leaderToken = Guid.NewGuid();
    private bool _wasLeader;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_node.RunLeader)
        {
            return;
        }

        logger.LogInformation("Leader campaigner started on node {NodeId}.", _node.NodeId);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
                await Task.Delay(_engine.LeaderLoop, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Leader loop error on node {NodeId}.", _node.NodeId);
                try { await Task.Delay(_engine.LeaderLoop, stoppingToken); } catch (OperationCanceledException) { break; }
            }
        }

        await ResignAsync();
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var election = sp.GetRequiredService<ILeaderElectionStore>();

        var now = clock.UtcNow;
        var view = await election.TryAcquireOrRenewAsync(_node.NodeId, _leaderToken, now, _engine.LeaderTtl, ct);
        bool isLeader = view.IsHeld && view.Owner == _node.NodeId;

        if (isLeader && !_wasLeader)
        {
            logger.LogInformation("Node {NodeId} acquired leadership (fencing token {Token}).", _node.NodeId, view.FencingToken);
            metrics.LeadershipChanged(_node.NodeId);
        }
        else if (!isLeader && _wasLeader)
        {
            logger.LogWarning("Node {NodeId} lost leadership.", _node.NodeId);
        }
        _wasLeader = isLeader;

        if (!isLeader)
        {
            return;
        }

        await RunLeaderDutiesAsync(sp, now, ct);
    }

    private async Task RunLeaderDutiesAsync(IServiceProvider sp, DateTimeOffset now, CancellationToken ct)
    {
        var materializer = sp.GetRequiredService<SchedulerMaterializer>();
        var runs = sp.GetRequiredService<IJobRunStore>();
        var registry = sp.GetRequiredService<IWorkerRegistry>();
        var runLogs = sp.GetRequiredService<IRunLogStore>();
        var deadLetters = sp.GetRequiredService<IDeadLetterStore>();

        await materializer.MaterializeAsync(ct);

        int reclaimed = await runs.ReclaimExpiredLeasesAsync(now, ct);
        if (reclaimed > 0)
        {
            metrics.LeaseExpired(reclaimed);
            logger.LogInformation("Leader reclaimed {Count} expired-lease run(s).", reclaimed);
        }

        int deadNodes = await registry.ReapDeadNodesAsync(now, _engine.NodeTtl, ct);
        if (deadNodes > 0)
        {
            logger.LogInformation("Leader marked {Count} node(s) dead.", deadNodes);
        }

        var retentionCutoff = now - _engine.Retention;
        await runs.PruneAsync(retentionCutoff, ct);
        await runLogs.PruneAsync(retentionCutoff, ct);

        metrics.SetDlqDepth(await deadLetters.CountAsync(includeReplayed: false, ct));
        metrics.SetQueueDepth(await runs.CountByStateAsync(Domain.RunState.Pending, ct));
    }

    private async Task ResignAsync()
    {
        if (!_wasLeader)
        {
            return;
        }
        try
        {
            using var scope = scopeFactory.CreateScope();
            var election = scope.ServiceProvider.GetRequiredService<ILeaderElectionStore>();
            await election.ReleaseAsync(_node.NodeId, _leaderToken, CancellationToken.None);
            logger.LogInformation("Node {NodeId} released leadership on shutdown.", _node.NodeId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Node {NodeId} failed to release leadership cleanly.", _node.NodeId);
        }
    }
}
