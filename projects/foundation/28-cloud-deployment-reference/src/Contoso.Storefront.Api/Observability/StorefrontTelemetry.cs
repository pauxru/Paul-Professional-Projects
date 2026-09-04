using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

namespace Contoso.Storefront.Api.Observability;

public static class StorefrontTelemetry
{
    public const string SourceName = "Contoso.Storefront";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName, "1.0.0");
}

public sealed record MetricsSnapshot(long Requests, long Errors, double ErrorRate, double P95LatencyMs);

public sealed class StorefrontMetrics
{
    private readonly Counter<long> _requestCounter =
        StorefrontTelemetry.Meter.CreateCounter<long>("storefront.requests");
    private readonly Counter<long> _errorCounter =
        StorefrontTelemetry.Meter.CreateCounter<long>("storefront.errors");
    private readonly Histogram<double> _duration =
        StorefrontTelemetry.Meter.CreateHistogram<double>("storefront.request.duration", "ms");
    private readonly object _gate = new();
    private readonly Queue<double> _recentDurations = new();
    private long _requests;
    private long _errors;

    public void RecordRequest(double durationMs, int statusCode)
    {
        Interlocked.Increment(ref _requests);
        _requestCounter.Add(1, new KeyValuePair<string, object?>("http.status_code", statusCode));
        _duration.Record(durationMs);

        if (statusCode >= 500)
        {
            Interlocked.Increment(ref _errors);
            _errorCounter.Add(1, new KeyValuePair<string, object?>("http.status_code", statusCode));
        }

        lock (_gate)
        {
            _recentDurations.Enqueue(durationMs);
            while (_recentDurations.Count > 2_048)
            {
                _recentDurations.Dequeue();
            }
        }
    }

    public MetricsSnapshot Snapshot()
    {
        var requests = Interlocked.Read(ref _requests);
        var errors = Interlocked.Read(ref _errors);
        double[] durations;
        lock (_gate)
        {
            durations = _recentDurations.OrderBy(value => value).ToArray();
        }

        var p95 = durations.Length == 0
            ? 0
            : durations[(int)Math.Ceiling(durations.Length * 0.95) - 1];
        var errorRate = requests == 0 ? 0 : (double)errors / requests;
        return new MetricsSnapshot(requests, errors, errorRate, p95);
    }

    public string RenderPrometheus()
    {
        var snapshot = Snapshot();
        return string.Join(
            "\n",
            "# HELP storefront_requests_total Total requests observed by this instance.",
            "# TYPE storefront_requests_total counter",
            $"storefront_requests_total {snapshot.Requests}",
            "# HELP storefront_errors_total Requests completed with a 5xx status.",
            "# TYPE storefront_errors_total counter",
            $"storefront_errors_total {snapshot.Errors}",
            "# HELP storefront_error_rate Ratio of 5xx responses to total responses.",
            "# TYPE storefront_error_rate gauge",
            $"storefront_error_rate {snapshot.ErrorRate.ToString("0.000000", CultureInfo.InvariantCulture)}",
            "# HELP storefront_request_duration_ms_p95 Rolling p95 request latency.",
            "# TYPE storefront_request_duration_ms_p95 gauge",
            $"storefront_request_duration_ms_p95 {snapshot.P95LatencyMs.ToString("0.000", CultureInfo.InvariantCulture)}",
            string.Empty);
    }
}
