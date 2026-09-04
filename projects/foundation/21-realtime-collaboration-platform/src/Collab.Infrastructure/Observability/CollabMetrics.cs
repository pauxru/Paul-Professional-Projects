using System.Diagnostics.Metrics;
using Collab.Application.Abstractions;

namespace Collab.Infrastructure.Observability;

/// <summary>
/// Collaboration-specific telemetry over a <see cref="Meter"/>. Exposes: connected-clients gauge,
/// operations applied (ops/sec is derived by the collector), apply/transform duration histogram,
/// resync counter and rejected-operations counter. The API wires this Meter into OpenTelemetry.
/// </summary>
public sealed class CollabMetrics : ICollabMetrics, IDisposable
{
    public const string MeterName = "Collab.Collaboration";

    private readonly Meter _meter;
    private readonly Counter<long> _opsApplied;
    private readonly Counter<long> _rejected;
    private readonly Counter<long> _resyncs;
    private readonly Histogram<double> _applyDuration;
    private int _connectedClients;

    public CollabMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");
        _opsApplied = _meter.CreateCounter<long>("collab.operations.applied", unit: "ops",
            description: "Total CRDT operations applied by the server.");
        _rejected = _meter.CreateCounter<long>("collab.operations.rejected", unit: "ops",
            description: "Total operation submissions rejected.");
        _resyncs = _meter.CreateCounter<long>("collab.resync.count", unit: "events",
            description: "Total client resync operations served.");
        _applyDuration = _meter.CreateHistogram<double>("collab.apply.duration", unit: "ms",
            description: "Duration of applying and transforming a submitted change set.");
        _meter.CreateObservableGauge("collab.clients.connected",
            () => Volatile.Read(ref _connectedClients), unit: "clients",
            description: "Currently connected hub clients.");
    }

    public void RecordApply(int operationCount, double milliseconds)
    {
        _opsApplied.Add(operationCount);
        _applyDuration.Record(milliseconds);
    }

    public void RecordRejected() => _rejected.Add(1);
    public void RecordResync() => _resyncs.Add(1);
    public void ClientConnected() => Interlocked.Increment(ref _connectedClients);
    public void ClientDisconnected() => Interlocked.Decrement(ref _connectedClients);

    public void Dispose() => _meter.Dispose();
}
