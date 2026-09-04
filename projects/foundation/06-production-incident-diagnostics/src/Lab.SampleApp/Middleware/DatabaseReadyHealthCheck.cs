using Lab.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lab.SampleApp.Middleware;

public sealed class DatabaseReadyHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<LogisticsDbContext>();
            return await dbContext.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("SQLite connection is available.")
                : HealthCheckResult.Unhealthy("SQLite connection could not be opened.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("SQLite readiness check failed.", exception);
        }
    }
}
