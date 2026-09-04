using Azure.Monitor.OpenTelemetry.Exporter;
using Contoso.Storefront.Application.Configuration;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Contoso.Storefront.Api.Observability;

public static class ObservabilitySetup
{
    public static IServiceCollection AddStorefrontObservability(
        this IServiceCollection services,
        WebApplicationBuilder builder)
    {
        var settings = builder.Configuration
            .GetSection(ObservabilityOptions.SectionName)
            .Get<ObservabilityOptions>() ?? new ObservabilityOptions();
        var serviceVersion =
            typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var instance =
            Environment.GetEnvironmentVariable("CONTAINER_APP_REVISION") ??
            Environment.MachineName;
        services.AddOpenTelemetry()
            .ConfigureResource(resourceBuilder => resourceBuilder
                .AddService(
                    serviceName: settings.ServiceName,
                    serviceVersion: serviceVersion,
                    serviceInstanceId: instance)
                .AddAttributes(
                [
                    new KeyValuePair<string, object>(
                        "deployment.environment",
                        builder.Environment.EnvironmentName),
                    new KeyValuePair<string, object>("service.instance.id", instance)
                ]))
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(StorefrontTelemetry.SourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation();
                ConfigureTraceExporter(tracing, settings);
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(StorefrontTelemetry.SourceName)
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
                ConfigureMetricExporter(metrics, settings);
            });

        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.ParseStateValues = true;
            ConfigureLogExporter(logging, settings);
        });

        services.AddSingleton<StorefrontMetrics>();
        return services;
    }

    private static void ConfigureTraceExporter(
        TracerProviderBuilder builder,
        ObservabilityOptions settings)
    {
        switch (settings.Exporter.ToLowerInvariant())
        {
            case "console":
                builder.AddConsoleExporter();
                break;
            case "otlp":
                builder.AddOtlpExporter(options => options.Endpoint = new Uri(settings.OtlpEndpoint!));
                break;
            case "azuremonitor":
                builder.AddAzureMonitorTraceExporter(
                    options => options.ConnectionString = settings.ApplicationInsightsConnectionString);
                break;
        }
    }

    private static void ConfigureMetricExporter(
        MeterProviderBuilder builder,
        ObservabilityOptions settings)
    {
        switch (settings.Exporter.ToLowerInvariant())
        {
            case "console":
                builder.AddConsoleExporter();
                break;
            case "otlp":
                builder.AddOtlpExporter(options => options.Endpoint = new Uri(settings.OtlpEndpoint!));
                break;
            case "azuremonitor":
                builder.AddAzureMonitorMetricExporter(
                    options => options.ConnectionString = settings.ApplicationInsightsConnectionString);
                break;
        }
    }

    private static void ConfigureLogExporter(
        OpenTelemetryLoggerOptions options,
        ObservabilityOptions settings)
    {
        switch (settings.Exporter.ToLowerInvariant())
        {
            case "console":
                options.AddConsoleExporter();
                break;
            case "otlp":
                options.AddOtlpExporter(exporter => exporter.Endpoint = new Uri(settings.OtlpEndpoint!));
                break;
            case "azuremonitor":
                options.AddAzureMonitorLogExporter(
                    exporter => exporter.ConnectionString = settings.ApplicationInsightsConnectionString);
                break;
        }
    }
}
