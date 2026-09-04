using System.Diagnostics;
using JobScheduler.Application.Abstractions;
using JobScheduler.Application.Options;
using JobScheduler.Application.Services;
using JobScheduler.Domain;
using JobScheduler.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JobScheduler.Infrastructure.Engine;

/// <summary>
/// Executes a single claimed run to a terminal outcome. Owns the per-run timeout, the heartbeat
/// loop that extends the lease (and observes cooperative cancellation), the fencing-guarded
/// write-back, and the retry/dead-letter decision. Handler code is the only payload-driven code
/// that runs, and it always receives a <see cref="CancellationToken"/> for timeout/cancel.
/// </summary>
public sealed class RunExecutor(
    IServiceScopeFactory scopeFactory,
    IJobRunStore runs,
    IJobDefinitionStore definitions,
    IRunLogStore runLogs,
    IDeadLetterStore deadLetters,
    IHandlerRegistry handlers,
    IJobCircuitBreaker circuitBreaker,
    DagOrchestrator dag,
    IClock clock,
    ISchedulerMetrics metrics,
    IOptions<EngineOptions> engineOptions,
    ILogger<RunExecutor> logger)
{
    private readonly EngineOptions _engine = engineOptions.Value;

    /// <summary>
    /// Runs the claimed job identified by <paramref name="runId"/>. <paramref name="hardStop"/> is
    /// the host shutdown token; it participates in the handler's cancellation but a clean shutdown
    /// prefers to let in-flight work finish (see <c>WorkerService</c>).
    /// </summary>
    public async Task ExecuteAsync(Guid runId, Guid leaseToken, long fencingToken, string nodeId, CancellationToken hardStop)
    {
        var startNow = clock.UtcNow;
        if (!await runs.TryStartAsync(runId, leaseToken, startNow, hardStop))
        {
            logger.LogWarning("Run {RunId} could not start (lease lost before execution).", runId);
            return;
        }

        var run = await runs.GetAsync(runId, hardStop);
        if (run is null)
        {
            return;
        }

        var def = await definitions.GetAsync(run.JobDefinitionId, hardStop);
        int timeoutSeconds = def?.TimeoutSeconds ?? 300;

        await AppendLogAsync(run, "Information", $"Attempt {run.AttemptCount} started on node {nodeId}.", nodeId, hardStop);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var leaseLostCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            hardStop, timeoutCts.Token, leaseLostCts.Token);

        var leaseLost = false;
        var cancelRequested = false;

        using var heartbeatStop = new CancellationTokenSource();
        var heartbeatTask = HeartbeatLoopAsync(
            runId, leaseToken,
            onLost: () => { leaseLost = true; leaseLostCts.Cancel(); },
            onCancel: () => { cancelRequested = true; leaseLostCts.Cancel(); },
            heartbeatStop.Token);

        HandlerOutcome? outcome = null;
        Exception? failure = null;
        var sw = Stopwatch.StartNew();

        try
        {
            var handler = handlers.Resolve(run.HandlerType);
            var context = new JobExecutionContext(
                run.Id, run.JobDefinitionId, run.JobName, run.HandlerType, run.PayloadJson,
                run.AttemptCount, run.IdempotencyKey, run.CorrelationId, nodeId);

            outcome = await handler.ExecuteAsync(context, linkedCts.Token);
            await FlushHandlerLogsAsync(run, context, nodeId, hardStop);
        }
        catch (OperationCanceledException)
        {
            // Distinguish timeout vs cooperative cancel vs lease-loss below.
        }
        catch (HandlerNotRegisteredException ex)
        {
            failure = ex;
            outcome = HandlerOutcome.Fail(ex.Message, retriable: false);
        }
        catch (Exception ex)
        {
            failure = ex;
            outcome = HandlerOutcome.Fail(ex.Message, retriable: true);
        }
        finally
        {
            sw.Stop();
            heartbeatStop.Cancel();
            try { await heartbeatTask; } catch (OperationCanceledException) { }
        }

        var finishNow = clock.UtcNow;

        // Lease loss takes precedence: another node now owns the run; we must not write anything back.
        if (leaseLost)
        {
            logger.LogWarning("Run {RunId} lost its lease during execution; abandoning write-back (fencing).", runId);
            metrics.RecordRunDuration(run.JobName, sw.Elapsed.TotalSeconds, success: false);
            return;
        }

        if (timeoutCts.IsCancellationRequested && outcome is null)
        {
            await CompleteFailureAsync(run, fencingToken, RunState.TimedOut, "Run exceeded its timeout.", nodeId, finishNow, def, retriable: true, hardStop);
            metrics.RecordRunDuration(run.JobName, sw.Elapsed.TotalSeconds, success: false);
            return;
        }

        if (cancelRequested && outcome is null)
        {
            var completed = await runs.TryCompleteAsync(runId, fencingToken, RunState.Cancelled, null, "Cancelled by request.", finishNow, hardStop);
            await AppendLogAsync(run, "Warning", "Run cancelled cooperatively.", nodeId, hardStop);
            if (completed)
            {
                circuitBreaker.RecordSuccess(run.JobDefinitionId); // a cancel is not a job-type failure
            }
            metrics.RecordRunDuration(run.JobName, sw.Elapsed.TotalSeconds, success: false);
            return;
        }

        if (hardStop.IsCancellationRequested && outcome is null)
        {
            // Host is stopping and the handler was interrupted: leave the run for reclaim.
            logger.LogInformation("Run {RunId} interrupted by shutdown; leaving for reclaim.", runId);
            return;
        }

        outcome ??= HandlerOutcome.Fail("Handler produced no outcome.", retriable: true);

        if (outcome.Succeeded)
        {
            var completed = await runs.TryCompleteAsync(runId, fencingToken, RunState.Succeeded, outcome.Output, null, finishNow, hardStop);
            if (!completed)
            {
                logger.LogWarning("Run {RunId} succeeded but write-back was fenced out (superseded).", runId);
                return;
            }

            circuitBreaker.RecordSuccess(run.JobDefinitionId);
            await AppendLogAsync(run, "Information", "Run succeeded.", nodeId, hardStop);
            metrics.RecordRunDuration(run.JobName, sw.Elapsed.TotalSeconds, success: true);

            var succeededRun = await runs.GetAsync(runId, hardStop);
            if (succeededRun is not null)
            {
                await dag.OnRunSucceededAsync(succeededRun, hardStop);
            }
            return;
        }

        await CompleteFailureAsync(run, fencingToken, RunState.Failed, outcome.Error ?? "Handler failed.", nodeId, finishNow, def, outcome.Retriable, hardStop);
        metrics.RecordRunDuration(run.JobName, sw.Elapsed.TotalSeconds, success: false);
        if (failure is not null)
        {
            logger.LogError(failure, "Run {RunId} ({Job}) failed.", runId, run.JobName);
        }
    }

    private async Task CompleteFailureAsync(
        JobRun run, long fencingToken, RunState failState, string error, string nodeId,
        DateTimeOffset now, JobDefinition? def, bool retriable, CancellationToken ct)
    {
        var completed = await runs.TryCompleteAsync(run.Id, fencingToken, failState, null, error, now, ct);
        if (!completed)
        {
            logger.LogWarning("Run {RunId} failure write-back was fenced out (superseded).", run.Id);
            return;
        }

        circuitBreaker.RecordFailure(run.JobDefinitionId, now);
        await AppendLogAsync(run, "Error", $"{failState}: {error}", nodeId, ct);

        var policy = def?.BuildRetryPolicy()
            ?? new RetryPolicy(RetryStrategy.ExponentialJitter, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(300), run.MaxAttempts, 0.2);
        var decision = RetryDecider.Decide(run.AttemptCount, policy, now, retriable);

        if (decision.ShouldRetry && decision.NextScheduledAt is { } next)
        {
            await runs.ScheduleRetryAsync(run.Id, next, ct);
            await AppendLogAsync(run, "Warning", decision.Reason, nodeId, ct);
        }
        else
        {
            await runs.MarkDeadLetteredAsync(run.Id, ct);
            var dlqRun = await runs.GetAsync(run.Id, ct) ?? run;
            await deadLetters.AddAsync(DeadLetterEntry.FromRun(dlqRun, decision.Reason, now), ct);
            await deadLetters.SaveChangesAsync(ct);
            await AppendLogAsync(run, "Error", $"Dead-lettered: {decision.Reason}", nodeId, ct);
        }
    }

    private async Task HeartbeatLoopAsync(
        Guid runId, Guid leaseToken, Action onLost, Action onCancel, CancellationToken stop)
    {
        using var scope = scopeFactory.CreateScope();
        var hbRuns = scope.ServiceProvider.GetRequiredService<IJobRunStore>();
        var interval = _engine.Heartbeat;

        while (!stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var now = clock.UtcNow;
            HeartbeatOutcome hb;
            try
            {
                hb = await hbRuns.HeartbeatAsync(runId, leaseToken, now + _engine.Lease, stop);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (!hb.LeaseHeld)
            {
                metrics.LeaseExpired();
                onLost();
                break;
            }
            if (hb.CancelRequested)
            {
                onCancel();
                break;
            }
        }
    }

    private async Task AppendLogAsync(JobRun run, string level, string message, string nodeId, CancellationToken ct)
    {
        var log = RunLog.For(run.Id, level, message, run.CorrelationId, nodeId, run.AttemptCount, clock.UtcNow);
        await runLogs.AppendAsync(log, ct);
    }

    private async Task FlushHandlerLogsAsync(JobRun run, JobExecutionContext context, string nodeId, CancellationToken ct)
    {
        if (context.Logs.Count == 0)
        {
            return;
        }
        var now = clock.UtcNow;
        var logs = context.Logs.Select(l => RunLog.For(run.Id, l.Level, l.Message, run.CorrelationId, nodeId, run.AttemptCount, now));
        await runLogs.AppendRangeAsync(logs, ct);
    }
}
