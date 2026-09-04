using CloudCostObservability.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace CloudCostObservability.Api;

public sealed class SqliteReadinessCheck(FinOpsDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("SQLite connection is available.")
            : HealthCheckResult.Unhealthy("SQLite connection is unavailable.");
}
