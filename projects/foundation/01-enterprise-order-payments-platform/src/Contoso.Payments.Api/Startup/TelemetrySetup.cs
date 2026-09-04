using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

using Contoso.Payments.Infrastructure.Observability;

namespace Contoso.Payments.Api.Startup;

public static class TelemetrySetup
{
    public static WebApplicationBuilder AddPaymentsTelemetry(this WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<PaymentMetrics>();
        builder.Services.AddSingleton<ActivitySources>();

        // Skip OpenTelemetry hosted service in Testing to avoid noisy diagnostics.
        if (builder.Environment.IsEnvironment("Testing")) return builder;

        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(TelemetryConstants.SourceName, serviceVersion: "1.0.0"))
            .WithTracing(t => t
                .AddSource(TelemetryConstants.SourceName)
                .AddAspNetCoreInstrumentation()
                .AddConsoleExporter())
            .WithMetrics(m => m
                .AddMeter(TelemetryConstants.SourceName)
                .AddAspNetCoreInstrumentation()
                .AddConsoleExporter());
        return builder;
    }
}
