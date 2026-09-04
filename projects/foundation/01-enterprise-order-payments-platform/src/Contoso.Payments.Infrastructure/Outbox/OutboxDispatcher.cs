using System.Diagnostics;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Common;
using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Infrastructure.Observability;
using Contoso.Payments.Infrastructure.Persistence;

namespace Contoso.Payments.Infrastructure.Outbox;

/// <summary>
/// Polls the outbox table and publishes messages to <see cref="IEventBus"/>.  Failures increment
/// <c>Attempts</c> and reschedule with exponential backoff + jitter.  After
/// <c>MaxAttempts</c> the row is copied to the dead-letter table and marked dispatched so it
/// leaves the poll set.
/// </summary>
public sealed class OutboxDispatcher : BackgroundService
{
    private static readonly ActivitySource Activity = new(TelemetryConstants.SourceName);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<OutboxOptions> _options;
    private readonly ILogger<OutboxDispatcher> _log;
    private readonly PaymentMetrics _metrics;

    public OutboxDispatcher(IServiceScopeFactory scopeFactory,
        IOptionsMonitor<OutboxOptions> options,
        ILogger<OutboxDispatcher> log,
        PaymentMetrics metrics)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _log = log;
        _metrics = metrics;
    }

    /// <summary>Test hook — signals a single dispatch cycle to complete.</summary>
    public event Action<int>? BatchDispatched;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var dispatched = await DispatchOnceAsync(stoppingToken);
                BatchDispatched?.Invoke(dispatched);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex, "Outbox dispatcher iteration failed");
            }

            try
            {
                await Task.Delay(_options.CurrentValue.PollIntervalMilliseconds, stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Dispatch a single batch — public so tests can drive the pump deterministically.
    /// </summary>
    public async Task<int> DispatchOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IEventBus>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var opt = _options.CurrentValue;

        var now = clock.UtcNow;
        var batch = await db.OutboxMessages
            .Where(m => !m.Dispatched && m.NextAttemptAtUtc <= now)
            .OrderBy(m => m.NextAttemptAtUtc)
            .Take(opt.BatchSize)
            .ToListAsync(ct);

        if (batch.Count == 0) return 0;

        var dispatched = 0;
        foreach (var msg in batch)
        {
            using var activity = Activity.StartActivity("outbox.dispatch", ActivityKind.Producer);
            activity?.SetTag("outbox.topic", msg.Topic);
            activity?.SetTag("outbox.attempt", msg.Attempts + 1);
            var sw = Stopwatch.StartNew();
            try
            {
                await bus.PublishAsync(msg.Topic, msg.PayloadJson, ct);
                msg.Dispatched = true;
                msg.DispatchedAtUtc = now;
                msg.LastError = null;
                dispatched++;
            }
            catch (Exception ex)
            {
                msg.Attempts++;
                msg.LastError = ex.Message;
                var backoff = ComputeBackoff(opt, msg.Attempts);
                msg.NextAttemptAtUtc = now.AddMilliseconds(backoff);
                _log.LogWarning(ex, "Outbox message {Id} attempt {Attempts} failed; retrying in {Backoff}ms",
                    msg.Id, msg.Attempts, backoff);

                if (msg.Attempts >= opt.MaxAttempts)
                {
                    db.OutboxDeadLetters.Add(new OutboxDeadLetter
                    {
                        Id = Guid.NewGuid(),
                        OriginalMessageId = msg.Id,
                        Topic = msg.Topic,
                        PayloadJson = msg.PayloadJson,
                        LastError = msg.LastError ?? "unknown",
                        Attempts = msg.Attempts,
                        DeadLetteredAtUtc = now,
                        CorrelationId = msg.CorrelationId
                    });
                    msg.Dispatched = true;   // remove from the poll set
                    msg.DispatchedAtUtc = now;
                    _metrics.OutboxDeadLettered.Add(1);
                    _log.LogError("Outbox message {Id} dead-lettered after {Attempts} attempts", msg.Id, msg.Attempts);
                }
            }
            finally
            {
                sw.Stop();
                _metrics.OutboxDispatchDurationMs.Record(sw.Elapsed.TotalMilliseconds);
            }
        }

        await db.SaveChangesAsync(ct);
        return dispatched;
    }

    private static int ComputeBackoff(OutboxOptions opt, int attempts)
    {
        var baseMs = opt.BaseBackoffMilliseconds;
        var exp = Math.Min(attempts, 10);
        var deterministic = baseMs * (int)Math.Pow(2, exp - 1);
        var jitter = Random.Shared.Next(0, Math.Max(1, deterministic / 4));
        return deterministic + jitter;
    }
}
