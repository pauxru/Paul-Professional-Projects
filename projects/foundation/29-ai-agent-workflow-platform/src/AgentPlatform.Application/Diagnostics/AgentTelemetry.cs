using System.Diagnostics;

namespace AgentPlatform.Application.Diagnostics;

/// <summary>
/// The engine's OpenTelemetry <see cref="ActivitySource"/>. The span tree it produces mirrors the
/// durable execution trace: one span per run, with a child span per executed step. This is wired
/// into OpenTelemetry tracing in the API and is inert (no listeners) in tests.
/// </summary>
public static class AgentTelemetry
{
    public const string SourceName = "AgentPlatform.Engine";

    public static readonly ActivitySource Source = new(SourceName, "1.0.0");
}
