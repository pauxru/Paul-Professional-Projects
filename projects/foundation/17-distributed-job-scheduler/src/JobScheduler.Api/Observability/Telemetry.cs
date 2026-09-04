using JobScheduler.Infrastructure.Metrics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace JobScheduler.Api.Observability;

/// <summary>
/// Wires OpenTelemetry metrics and tracing. The custom <see cref="SchedulerMetrics.MeterName"/>
/// meter exposes claim latency, queue/DLQ depth, run-duration histograms per definition, and the
/// lease-expiry / leadership-change counters required by the brief.
/// </summary>
public static class Telemetry
{
    public static IServiceCollection AddSchedulerTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        bool consoleExporter = configuration.GetValue("OpenTelemetry:ConsoleExporter", false);

        var otel = services.AddOpenTelemetry();

        otel.ConfigureResource(r => r.AddService("jobscheduler-api"));

        otel.WithMetrics(metrics =>
        {
            metrics.AddMeter(SchedulerMetrics.MeterName);
            metrics.AddAspNetCoreInstrumentation();
            if (consoleExporter)
            {
                metrics.AddConsoleExporter();
            }
        });

        otel.WithTracing(tracing =>
        {
            tracing.AddAspNetCoreInstrumentation();
            if (consoleExporter)
            {
                tracing.AddConsoleExporter();
            }
        });

        return services;
    }
}
