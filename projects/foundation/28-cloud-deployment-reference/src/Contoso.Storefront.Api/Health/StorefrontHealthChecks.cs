using System.Text.Json;
using Contoso.Storefront.Api.Hosting;
using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Contoso.Storefront.Api.Health;

public sealed class ProcessAliveHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy("The process is alive."));
}

public sealed class DatabaseHealthCheck(StorefrontDbContext dbContext) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await dbContext.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Database connection succeeded.")
            : HealthCheckResult.Unhealthy("Database connection failed.");
}

public sealed class CacheHealthCheck(ICacheHealthProbe cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await cache.IsHealthyAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Cache probe succeeded.")
            : HealthCheckResult.Unhealthy("Cache probe failed.");
}

public sealed class MessageBusHealthCheck(IMessageBusHealthProbe bus) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await bus.IsHealthyAsync(cancellationToken)
            ? HealthCheckResult.Healthy("Message bus probe succeeded.")
            : HealthCheckResult.Unhealthy("Message bus probe failed.");
}

public sealed class MigrationsAppliedHealthCheck(IMigrationStatus migrationStatus) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        await migrationStatus.AreAllMigrationsAppliedAsync(cancellationToken)
            ? HealthCheckResult.Healthy("All known migrations are applied.")
            : HealthCheckResult.Unhealthy("One or more migrations are pending.");
}

public sealed class StartupHealthCheck(StartupState startupState) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var result = startupState.Status switch
        {
            ApplicationStartupStatus.Ready => HealthCheckResult.Healthy(startupState.Detail),
            ApplicationStartupStatus.Failed => HealthCheckResult.Unhealthy(startupState.Detail),
            _ => HealthCheckResult.Unhealthy(startupState.Detail)
        };
        return Task.FromResult(result);
    }
}

public static class StorefrontHealthChecks
{
    public static IServiceCollection AddStorefrontHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks()
            .AddCheck<ProcessAliveHealthCheck>("process", tags: ["live"])
            .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
            .AddCheck<CacheHealthCheck>("cache", tags: ["ready"])
            .AddCheck<MessageBusHealthCheck>("bus", tags: ["ready"])
            .AddCheck<MigrationsAppliedHealthCheck>("migrations", tags: ["ready"])
            .AddCheck<StartupHealthCheck>("startup", tags: ["startup"]);
        return services;
    }

    public static WebApplication MapStorefrontHealthChecks(this WebApplication app)
    {
        app.MapHealthChecks(
            "/health/live",
            OptionsFor("live"));
        app.MapHealthChecks(
            "/health/ready",
            OptionsFor("ready"));
        app.MapHealthChecks(
            "/health/startup",
            OptionsFor("startup"));
        return app;
    }

    private static HealthCheckOptions OptionsFor(string tag) => new()
    {
        Predicate = registration => registration.Tags.Contains(tag),
        ResultStatusCodes =
        {
            [HealthStatus.Healthy] = StatusCodes.Status200OK,
            [HealthStatus.Degraded] = StatusCodes.Status200OK,
            [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable
        },
        ResponseWriter = WriteResponseAsync
    };

    private static Task WriteResponseAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            durationMs = report.TotalDuration.TotalMilliseconds,
            checks = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    status = entry.Value.Status.ToString(),
                    description = entry.Value.Description,
                    durationMs = entry.Value.Duration.TotalMilliseconds
                })
        }));
    }
}
