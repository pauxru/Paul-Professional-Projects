using FieldOps.Application;
using FieldOps.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace FieldOps.Infrastructure;

public sealed class TenantSaveChangesInterceptor(
    ITenantContext tenantContext,
    ILogger<TenantSaveChangesInterceptor> logger) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        Inspect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Inspect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Inspect(DbContext? dbContext)
    {
        if (dbContext is null) return;

        foreach (var entry in dbContext.ChangeTracker.Entries())
        {
            if (entry.Entity is IAppendOnly && entry.State is EntityState.Modified or EntityState.Deleted)
            {
                logger.LogWarning("SECURITY append-only mutation rejected for {EntityType}", entry.Metadata.ClrType.Name);
                throw new ForbiddenOperationException("Audit records are append-only.");
            }

            if (entry.Entity is not ITenantOwned tenantOwned
                || entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted))
            {
                continue;
            }

            var ambientTenant = tenantContext.RequiredTenantId;
            if (entry.State == EntityState.Added && tenantOwned.TenantId == Guid.Empty)
            {
                tenantOwned.TenantId = ambientTenant;
            }

            var originalTenant = entry.State == EntityState.Added
                ? tenantOwned.TenantId
                : (Guid)entry.Property(nameof(ITenantOwned.TenantId)).OriginalValue!;

            if (tenantOwned.TenantId != ambientTenant || originalTenant != ambientTenant)
            {
                logger.LogWarning(
                    "SECURITY cross-tenant write rejected. Entity={EntityType} AmbientTenant={AmbientTenant} EntityTenant={EntityTenant} OriginalTenant={OriginalTenant}",
                    entry.Metadata.ClrType.Name,
                    ambientTenant,
                    tenantOwned.TenantId,
                    originalTenant);
                throw new CrossTenantAccessException("A cross-tenant write was rejected by the persistence boundary.");
            }
        }
    }
}
