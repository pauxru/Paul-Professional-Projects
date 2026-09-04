using Contoso.Storefront.Application.Ports;
using Contoso.Storefront.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Contoso.Storefront.Infrastructure.Adapters;

public sealed class DatabaseMigrationStatus(StorefrontDbContext dbContext) : IMigrationStatus
{
    public async Task<bool> AreAllMigrationsAppliedAsync(CancellationToken cancellationToken)
    {
        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            return false;
        }

        var pending = await dbContext.Database.GetPendingMigrationsAsync(cancellationToken);
        return !pending.Any();
    }
}

public sealed class CompositeStartupDependencyProbe(
    StorefrontDbContext dbContext,
    ICacheHealthProbe cache,
    IMessageBusHealthProbe bus,
    IMigrationStatus migrationStatus) : IStartupDependencyProbe
{
    public async Task<StartupProbeResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (!await dbContext.Database.CanConnectAsync(cancellationToken))
        {
            return StartupProbeResult.Unhealthy("Database is unavailable.");
        }

        if (!await migrationStatus.AreAllMigrationsAppliedAsync(cancellationToken))
        {
            return StartupProbeResult.Unhealthy("Database migrations are pending.");
        }

        if (!await cache.IsHealthyAsync(cancellationToken))
        {
            return StartupProbeResult.Unhealthy("Cache is unavailable.");
        }

        if (!await bus.IsHealthyAsync(cancellationToken))
        {
            return StartupProbeResult.Unhealthy("Message bus is unavailable.");
        }

        return StartupProbeResult.Healthy();
    }
}
