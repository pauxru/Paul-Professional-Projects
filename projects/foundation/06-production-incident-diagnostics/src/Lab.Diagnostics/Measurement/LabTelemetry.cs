using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Lab.Diagnostics.Measurement;

public static class LabTelemetry
{
    public const string ActivitySourceName = "ProductionIncidentDiagnostics.Northstar";
    public const string MeterName = "ProductionIncidentDiagnostics.Northstar";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);
    public static readonly Meter Meter = new(MeterName);
    public static readonly Counter<long> OrdersBooked = Meter.CreateCounter<long>("northstar.orders.booked");
    public static readonly Histogram<double> RequestLatencyMilliseconds =
        Meter.CreateHistogram<double>("northstar.request.duration", unit: "ms");
}
