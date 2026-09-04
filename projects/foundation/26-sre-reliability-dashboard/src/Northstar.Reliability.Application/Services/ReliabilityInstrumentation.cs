using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Northstar.Reliability.Application.Services;

public static class ReliabilityInstrumentation
{
    public const string SourceName = "Northstar.Reliability";
    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName, "1.0.0");
    public static readonly Counter<long> MetricsIngested = Meter.CreateCounter<long>("reliability.metrics.ingested");
    public static readonly Counter<long> AlertsEvaluated = Meter.CreateCounter<long>("reliability.alerts.evaluated");
    public static readonly Counter<long> IncidentsDeclared = Meter.CreateCounter<long>("reliability.incidents.declared");
}
