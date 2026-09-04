using EnterpriseSearch.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace EnterpriseSearch.Api;

public sealed class SqliteReadinessHealthCheck(IServiceScopeFactory scopeFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var database = scope.ServiceProvider.GetRequiredService<SearchDbContext>();
            return await database.Database.CanConnectAsync(cancellationToken)
                ? HealthCheckResult.Healthy("SQLite is reachable.")
                : HealthCheckResult.Unhealthy("SQLite cannot be reached.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy("SQLite readiness check failed.", exception);
        }
    }
}
