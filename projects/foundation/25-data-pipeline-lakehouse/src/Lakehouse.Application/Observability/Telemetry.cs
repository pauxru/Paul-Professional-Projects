using System.Diagnostics;

namespace Lakehouse.Application.Observability;

/// <summary>
/// The single <see cref="ActivitySource"/> for pipeline tracing. The API wires OpenTelemetry to listen
/// to this source, so every DAG task becomes a span with row-count and status tags without the
/// Application layer depending on any OTel package.
/// </summary>
public static class Telemetry
{
    public const string SourceName = "Lakehouse.Pipeline";
    public static readonly ActivitySource Source = new(SourceName);
}
