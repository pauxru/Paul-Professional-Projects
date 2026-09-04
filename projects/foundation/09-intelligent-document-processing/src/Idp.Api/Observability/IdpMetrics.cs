using System.Diagnostics.Metrics;
using Idp.Application.Metrics;
using Microsoft.Extensions.DependencyInjection;

namespace Idp.Api.Observability;

/// <summary>
/// OpenTelemetry instruments for the pipeline. Histograms capture pipeline stage duration and the
/// extraction confidence distribution; counters track processed / auto-approved documents; observable
/// gauges surface the live straight-through-processing rate and the review-queue depth (read on
/// scrape from the database). The meter name is registered with the OTel MeterProvider in Program.
/// </summary>
public sealed class IdpMetrics : IDisposable
{
    public const string MeterName = "Idp.Pipeline";

    private readonly Meter _meter;

    public Histogram<double> PipelineDurationMs { get; }
    public Histogram<double> ExtractionConfidence { get; }
    public Counter<long> DocumentsProcessed { get; }
    public Counter<long> DocumentsAutoApproved { get; }

    public IdpMetrics(IServiceScopeFactory scopeFactory)
    {
        _meter = new Meter(MeterName, "1.0.0");
        PipelineDurationMs = _meter.CreateHistogram<double>(
            "idp.pipeline.duration.ms", "ms", "End-to-end pipeline processing duration.");
        ExtractionConfidence = _meter.CreateHistogram<double>(
            "idp.extraction.confidence", "ratio", "Document-level extraction confidence.");
        DocumentsProcessed = _meter.CreateCounter<long>(
            "idp.documents.processed", "documents", "Documents that reached a routing decision.");
        DocumentsAutoApproved = _meter.CreateCounter<long>(
            "idp.documents.autoapproved", "documents", "Documents auto-approved (straight-through).");

        _meter.CreateObservableGauge("idp.stp.rate", () =>
        {
            var snapshot = Snapshot(scopeFactory);
            return new Measurement<double>(snapshot?.StraightThroughRate ?? 0.0);
        }, "ratio", "Straight-through-processing rate over processed documents.");

        _meter.CreateObservableGauge("idp.review.queue.depth", () =>
        {
            var snapshot = Snapshot(scopeFactory);
            return new Measurement<long>(snapshot?.ReviewQueueDepth ?? 0);
        }, "tasks", "Open review-queue depth.");
    }

    private static StpSnapshot? Snapshot(IServiceScopeFactory scopeFactory)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var metrics = scope.ServiceProvider.GetRequiredService<IStpMetricsService>();
            return metrics.ComputeAsync().GetAwaiter().GetResult();
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _meter.Dispose();
}
