using Lakehouse.Application.Abstractions;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Lakehouse.Api;

/// <summary>
/// Liveness/readiness probe: confirms the lake root is reachable and reports how many tables exist. Kept
/// cheap (a directory listing) so it can be polled frequently by orchestrators.
/// </summary>
public sealed class LakehouseHealthCheck(ILakehouse lake) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var tables = lake.ListTables();
            var data = new Dictionary<string, object> { ["root"] = lake.RootPath, ["tables"] = tables.Count };
            return Task.FromResult(HealthCheckResult.Healthy("Lakehouse reachable", data));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Lakehouse unreachable", ex));
        }
    }
}
