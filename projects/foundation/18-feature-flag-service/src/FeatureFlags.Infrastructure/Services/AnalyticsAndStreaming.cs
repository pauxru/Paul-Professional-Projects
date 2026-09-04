using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FeatureFlags.Application;
using FeatureFlags.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FeatureFlags.Infrastructure.Services;

public sealed class EfAnalyticsStore(FeatureFlagDbContext db) : IAnalyticsStore
{
    public async Task RecordEvaluationAsync(AnalyticsEvent evaluation, CancellationToken cancellationToken)
    {
        await AddEventAsync(evaluation, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var metric = await db.EvaluationMetrics.SingleOrDefaultAsync(item => item.ProjectKey == evaluation.ProjectKey
            && item.EnvironmentKey == evaluation.EnvironmentKey && item.FlagKey == evaluation.FlagKey
            && item.VariationIndex == evaluation.VariationIndex, cancellationToken);
        if (metric is null)
        {
            metric = new EvaluationMetricEntity
            {
                ProjectKey = evaluation.ProjectKey, EnvironmentKey = evaluation.EnvironmentKey, FlagKey = evaluation.FlagKey ?? string.Empty,
                VariationIndex = evaluation.VariationIndex, Count = 0, LastEvaluatedAt = evaluation.OccurredAt
            };
            db.EvaluationMetrics.Add(metric);
        }

        metric.Count++;
        metric.LastEvaluatedAt = evaluation.OccurredAt;
        metric.UniqueContextCount = await db.AnalyticsEvents.AsNoTracking().Where(item => item.ProjectKey == evaluation.ProjectKey
            && item.EnvironmentKey == evaluation.EnvironmentKey && item.FlagKey == evaluation.FlagKey
            && item.VariationIndex == evaluation.VariationIndex && item.Kind == "evaluation")
            .Select(item => item.ContextKey).Distinct().LongCountAsync(cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordEventsAsync(IReadOnlyList<AnalyticsEvent> events, CancellationToken cancellationToken)
    {
        foreach (var item in events)
        {
            if (string.Equals(item.Kind, "evaluation", StringComparison.Ordinal))
            {
                await RecordEvaluationAsync(item, cancellationToken);
            }
            else
            {
                await AddEventAsync(item, cancellationToken);
            }
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<FlagEvaluationMetric>> GetMetricsAsync(string projectKey, string environmentKey, CancellationToken cancellationToken) =>
        (await db.EvaluationMetrics.AsNoTracking().Where(item => item.ProjectKey == projectKey && item.EnvironmentKey == environmentKey)
            .OrderBy(item => item.FlagKey).ThenBy(item => item.VariationIndex).ToListAsync(cancellationToken))
        .Select(item => new FlagEvaluationMetric(item.ProjectKey, item.EnvironmentKey, item.FlagKey, item.VariationIndex, item.Count, item.LastEvaluatedAt, item.UniqueContextCount)).ToArray();

    public async Task<DateTimeOffset?> LastEvaluatedAsync(string projectKey, string environmentKey, string flagKey, CancellationToken cancellationToken)
    {
        var values = await db.EvaluationMetrics.AsNoTracking().Where(item => item.ProjectKey == projectKey && item.EnvironmentKey == environmentKey && item.FlagKey == flagKey)
            .Select(item => item.LastEvaluatedAt).ToListAsync(cancellationToken);
        return values.Count == 0 ? null : values.Max();
    }

    public async Task<IReadOnlyList<AnalyticsEvent>> GetEventsAsync(string projectKey, string environmentKey, CancellationToken cancellationToken) =>
        (await db.AnalyticsEvents.AsNoTracking().Where(item => item.ProjectKey == projectKey && item.EnvironmentKey == environmentKey)
            .ToListAsync(cancellationToken)).OrderBy(item => item.OccurredAt)
        .Select(item => new AnalyticsEvent(item.ProjectKey, item.EnvironmentKey, item.Kind, item.FlagKey, item.VariationIndex, item.ContextKey, item.MetricKey, item.NumericValue, item.OccurredAt)).ToArray();

    private async Task AddEventAsync(AnalyticsEvent item, CancellationToken cancellationToken)
    {
        db.AnalyticsEvents.Add(new AnalyticsEventEntity
        {
            ProjectKey = item.ProjectKey, EnvironmentKey = item.EnvironmentKey, Kind = item.Kind, FlagKey = item.FlagKey,
            VariationIndex = item.VariationIndex, ContextKey = item.ContextKey, MetricKey = item.MetricKey,
            NumericValue = item.NumericValue, OccurredAt = item.OccurredAt
        });
        await Task.CompletedTask;
    }
}

public sealed class ChannelConfigurationBroadcaster : IConfigurationBroadcaster
{
    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();

    public ValueTask PublishAsync(ConfigurationChanged change, CancellationToken cancellationToken)
    {
        foreach (var subscriber in _subscribers.Values.Where(item => string.Equals(item.ProjectKey, change.ProjectKey, StringComparison.Ordinal)
                     && string.Equals(item.EnvironmentKey, change.EnvironmentKey, StringComparison.Ordinal)))
        {
            subscriber.Channel.Writer.TryWrite(change);
        }
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<ConfigurationChanged> SubscribeAsync(string projectKey, string environmentKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateUnbounded<ConfigurationChanged>(new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
        _subscribers.TryAdd(id, new Subscriber(projectKey, environmentKey, channel));
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return change;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    private sealed record Subscriber(string ProjectKey, string EnvironmentKey, Channel<ConfigurationChanged> Channel);
}
