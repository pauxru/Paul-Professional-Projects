namespace NotificationPlatform.Infrastructure.Metrics;

using System.Diagnostics;
using System.Diagnostics.Metrics;

public interface INotificationMetrics
{
    ActivitySource ActivitySource { get; }
    void RecordQueued(string tenant, string channel);
    void RecordDispatched(string tenant, string channel, string provider);
    void RecordDelivered(string tenant, string channel);
    void RecordFailure(string tenant, string channel, string provider, string reason);
    void RecordDeadLettered(string tenant, string channel);
    void RecordSuppressed(string tenant, string channel);
    void RecordRenderDuration(double ms);
    void RecordSendDuration(double ms, string provider, string channel);
}

public sealed class NotificationMetrics : INotificationMetrics, IDisposable
{
    public const string ActivitySourceName = "NotificationPlatform";
    public const string MeterName = "NotificationPlatform";

    private readonly ActivitySource _activity;
    private readonly Meter _meter;
    private readonly Counter<long> _queued;
    private readonly Counter<long> _dispatched;
    private readonly Counter<long> _delivered;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _dead;
    private readonly Counter<long> _suppressed;
    private readonly Histogram<double> _renderDuration;
    private readonly Histogram<double> _sendDuration;

    public NotificationMetrics()
    {
        _activity = new ActivitySource(ActivitySourceName, "1.0.0");
        _meter = new Meter(MeterName, "1.0.0");
        _queued = _meter.CreateCounter<long>("notifications.queued");
        _dispatched = _meter.CreateCounter<long>("notifications.dispatched");
        _delivered = _meter.CreateCounter<long>("notifications.delivered");
        _failed = _meter.CreateCounter<long>("notifications.failed");
        _dead = _meter.CreateCounter<long>("notifications.dead_lettered");
        _suppressed = _meter.CreateCounter<long>("notifications.suppressed");
        _renderDuration = _meter.CreateHistogram<double>("notifications.render.duration.ms");
        _sendDuration = _meter.CreateHistogram<double>("notifications.send.duration.ms");
    }

    public ActivitySource ActivitySource => _activity;

    public void RecordQueued(string tenant, string channel)
        => _queued.Add(1, new KeyValuePair<string, object?>("tenant", tenant), new KeyValuePair<string, object?>("channel", channel));

    public void RecordDispatched(string tenant, string channel, string provider)
        => _dispatched.Add(1, new KeyValuePair<string, object?>("tenant", tenant), new KeyValuePair<string, object?>("channel", channel), new KeyValuePair<string, object?>("provider", provider));

    public void RecordDelivered(string tenant, string channel)
        => _delivered.Add(1, new KeyValuePair<string, object?>("tenant", tenant), new KeyValuePair<string, object?>("channel", channel));

    public void RecordFailure(string tenant, string channel, string provider, string reason)
        => _failed.Add(1, new KeyValuePair<string, object?>("tenant", tenant), new KeyValuePair<string, object?>("channel", channel), new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("reason", reason));

    public void RecordDeadLettered(string tenant, string channel)
        => _dead.Add(1, new KeyValuePair<string, object?>("tenant", tenant), new KeyValuePair<string, object?>("channel", channel));

    public void RecordSuppressed(string tenant, string channel)
        => _suppressed.Add(1, new KeyValuePair<string, object?>("tenant", tenant), new KeyValuePair<string, object?>("channel", channel));

    public void RecordRenderDuration(double ms) => _renderDuration.Record(ms);

    public void RecordSendDuration(double ms, string provider, string channel)
        => _sendDuration.Record(ms, new KeyValuePair<string, object?>("provider", provider), new KeyValuePair<string, object?>("channel", channel));

    public void Dispose()
    {
        _activity.Dispose();
        _meter.Dispose();
    }
}
