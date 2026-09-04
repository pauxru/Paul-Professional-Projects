using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Contoso.Payments.Infrastructure.Observability;

public static class TelemetryConstants
{
    public const string SourceName = "Contoso.Payments";
}

/// <summary>Business event counters exposed via <see cref="System.Diagnostics.Metrics.Meter"/>.</summary>
public sealed class PaymentMetrics : IDisposable
{
    public Meter Meter { get; }
    public Counter<long> PaymentsAuthorized { get; }
    public Counter<long> PaymentsFailed { get; }
    public Counter<long> OutboxDeadLettered { get; }
    public Counter<long> WebhooksAccepted { get; }
    public Counter<long> WebhooksRejected { get; }
    public Counter<long> IdempotencyReplays { get; }
    public Histogram<double> PaymentAuthorizeDurationMs { get; }
    public Histogram<double> OutboxDispatchDurationMs { get; }

    public PaymentMetrics()
    {
        Meter = new Meter(TelemetryConstants.SourceName);
        PaymentsAuthorized = Meter.CreateCounter<long>("payments_authorized_total");
        PaymentsFailed = Meter.CreateCounter<long>("payments_failed_total");
        OutboxDeadLettered = Meter.CreateCounter<long>("outbox_dead_lettered_total");
        WebhooksAccepted = Meter.CreateCounter<long>("webhooks_accepted_total");
        WebhooksRejected = Meter.CreateCounter<long>("webhooks_rejected_total");
        IdempotencyReplays = Meter.CreateCounter<long>("idempotency_replays_total");
        PaymentAuthorizeDurationMs = Meter.CreateHistogram<double>("payments_authorize_duration_ms", "ms");
        OutboxDispatchDurationMs = Meter.CreateHistogram<double>("outbox_dispatch_duration_ms", "ms");
    }

    public void Dispose() => Meter.Dispose();
}

public sealed class ActivitySources : IDisposable
{
    public ActivitySource Source { get; } = new(TelemetryConstants.SourceName);
    public void Dispose() => Source.Dispose();
}
