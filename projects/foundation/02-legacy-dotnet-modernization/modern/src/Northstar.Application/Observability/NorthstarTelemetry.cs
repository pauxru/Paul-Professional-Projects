using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Northstar.Application.Observability;

public static class NorthstarTelemetry
{
    public const string SourceName = "Northstar.Claims";

    public static readonly ActivitySource ActivitySource = new(SourceName);
    public static readonly Meter Meter = new(SourceName);
    public static readonly Counter<long> ClaimsIntakeCounter = Meter.CreateCounter<long>("northstar.claims.intake");
}
