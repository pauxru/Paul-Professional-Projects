using System.Diagnostics.Metrics;

namespace Iiot.Api;

public static class IiotMetrics
{
    public const string MeterName = "Iiot.Api";
    private static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> IngestedReadings = Meter.CreateCounter<long>("iiot.ingest.readings");
    public static readonly ObservableGauge<int> GatewayBufferDepth = Meter.CreateObservableGauge<int>(
        "iiot.gateway.buffer.depth",
        () => GatewayNetworkControl.CurrentBufferDepth);
    public static readonly Counter<long> AlertEvents = Meter.CreateCounter<long>("iiot.alert.events");
    public static readonly Histogram<double> CommandLatencyMs = Meter.CreateHistogram<double>("iiot.command.latency.ms");
}

public sealed class GatewayNetworkControl
{
    private int _online = 1;
    private static int _bufferDepth;

    public bool IsOnline => Volatile.Read(ref _online) == 1;
    public static int CurrentBufferDepth => Volatile.Read(ref _bufferDepth);

    public void SetOnline(bool online) => Interlocked.Exchange(ref _online, online ? 1 : 0);
    public void SetBufferDepth(int depth) => Interlocked.Exchange(ref _bufferDepth, depth);
}
