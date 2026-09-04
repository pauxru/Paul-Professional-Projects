using System.Diagnostics.Metrics;
using ExampleBank.Ledger.Application.Abstractions;

namespace ExampleBank.Ledger.Infrastructure.Telemetry;

/// <summary>
/// OpenTelemetry-backed implementation of <see cref="ILedgerMetrics"/>. Exposes posting latency,
/// entries posted (throughput), rejected imbalance attempts and hold expiries under the
/// <c>ExampleBank.Ledger</c> meter, which the host registers with the OTel metrics pipeline.
/// </summary>
public sealed class LedgerMetrics : ILedgerMetrics, IDisposable
{
    public const string MeterName = "ExampleBank.Ledger";

    private readonly Meter _meter;
    private readonly Histogram<double> _postingLatency;
    private readonly Counter<long> _entriesPosted;
    private readonly Counter<long> _imbalanceAttempts;
    private readonly Counter<long> _holdExpired;

    public LedgerMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _postingLatency = _meter.CreateHistogram<double>(
            "ledger.posting.latency", unit: "ms", description: "Latency of posting a journal entry.");
        _entriesPosted = _meter.CreateCounter<long>(
            "ledger.entries.posted", description: "Number of journal entries posted.");
        _imbalanceAttempts = _meter.CreateCounter<long>(
            "ledger.imbalance.attempts", description: "Number of rejected unbalanced/mixed-currency attempts.");
        _holdExpired = _meter.CreateCounter<long>(
            "ledger.holds.expired", description: "Number of holds expired by the background sweeper.");
    }

    public void RecordPostingLatency(double milliseconds, string entryType) =>
        _postingLatency.Record(milliseconds, new KeyValuePair<string, object?>("entry_type", entryType));

    public void EntryPosted(string entryType) =>
        _entriesPosted.Add(1, new KeyValuePair<string, object?>("entry_type", entryType));

    public void ImbalanceAttempt(string reason) =>
        _imbalanceAttempts.Add(1, new KeyValuePair<string, object?>("reason", reason));

    public void HoldExpired() => _holdExpired.Add(1);

    public void Dispose() => _meter.Dispose();
}
