using System.Diagnostics.Metrics;

namespace ReconEngine.Api.Observability;

/// <summary>
/// Custom reconciliation metrics exposed via a <see cref="Meter"/>: throughput (rows/sec) and match rate
/// per run as histograms, a counter of completed runs, and an observable gauge of currently-open
/// exceptions. The gauge reads a value the API updates after each run and on demand.
/// </summary>
public sealed class ReconMetrics : IDisposable
{
    public const string MeterName = "ReconEngine";

    private readonly Meter _meter;
    private readonly Counter<long> _runsCompleted;
    private readonly Histogram<double> _rowsPerSecond;
    private readonly Histogram<double> _matchRate;
    private long _openExceptions;

    public ReconMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _runsCompleted = _meter.CreateCounter<long>("recon.runs.completed", "runs", "Number of reconciliation runs completed.");
        _rowsPerSecond = _meter.CreateHistogram<double>("recon.run.rows_per_second", "rows/s", "Throughput of a reconciliation run.");
        _matchRate = _meter.CreateHistogram<double>("recon.run.match_rate", "ratio", "Fraction of records matched in a run.");
        _meter.CreateObservableGauge("recon.exceptions.open", () => Interlocked.Read(ref _openExceptions), "exceptions", "Currently open exceptions.");
    }

    public void RecordRun(int totalRecords, int matchedRecords, long durationMs)
    {
        _runsCompleted.Add(1);

        if (durationMs > 0)
            _rowsPerSecond.Record(totalRecords / (durationMs / 1000.0));

        if (totalRecords > 0)
            _matchRate.Record(matchedRecords / (double)totalRecords);
    }

    public void SetOpenExceptions(long count) => Interlocked.Exchange(ref _openExceptions, count);

    public void Dispose() => _meter.Dispose();
}
