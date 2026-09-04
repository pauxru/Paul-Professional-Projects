using System.Diagnostics;

namespace ReconEngine.Application.Observability;

/// <summary>
/// The application's tracing entry point. Stages of a reconciliation run start activities on this
/// source so an OpenTelemetry exporter configured in the host can emit one span per stage
/// (load → calculate → persist) under the parent run span.
/// </summary>
public static class ReconDiagnostics
{
    public const string SourceName = "ReconEngine.Application";

    public static readonly ActivitySource ActivitySource = new(SourceName);
}
