using FieldOps.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FieldOps.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFieldOpsInfrastructure(
        this IServiceCollection services,
        string connectionString,
        LocalObjectStoreOptions objectStore,
        BillingSimulatorOptions billing)
    {
        services.AddScoped<TenantContext>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<IMutableTenantContext>(sp => sp.GetRequiredService<TenantContext>());
        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddDbContext<FieldOpsDbContext>((sp, options) =>
            options.UseSqlite(connectionString)
                .AddInterceptors(sp.GetRequiredService<TenantSaveChangesInterceptor>()));

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<TenantCacheStore>();
        services.AddScoped<IAppCache, TenantAwareMemoryCache>();
        services.AddSingleton(objectStore);
        services.AddSingleton(billing);
        services.AddScoped<IObjectStore, LocalObjectStore>();

        services.AddScoped<IJobRepository, JobRepository>();
        services.AddScoped<UnsafeJobRepository>();
        services.AddScoped<IAssetRepository, AssetRepository>();
        services.AddScoped<IOrganizationRepository, OrganizationRepository>();
        services.AddScoped<IMembershipRepository, MembershipRepository>();
        services.AddScoped<IInvitationRepository, InvitationRepository>();
        services.AddScoped<IInspectionRepository, InspectionRepository>();
        services.AddScoped<IFeatureFlagStore, FeatureFlagStore>();
        services.AddScoped<IUsageMeterStore, UsageMeterStore>();
        services.AddScoped<IWebhookReceiptStore, WebhookReceiptStore>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IBillingProvider, BillingProviderSimulator>();

        services.AddScoped<IEntitlementService, EntitlementService>();
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<JobApplicationService>();
        services.AddScoped<InspectionService>();
        services.AddScoped<FeatureFlagService>();
        services.AddScoped<UsageQuotaService>();
        services.AddScoped<InvitationService>();
        services.AddScoped<BillingService>();
        services.AddScoped(sp => new BillingWebhookProcessor(
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IWebhookReceiptStore>(),
            sp.GetRequiredService<IOrganizationRepository>(),
            billing.WebhookSecret,
            TimeSpan.FromMinutes(billing.SignatureToleranceMinutes),
            billing.SuspendAfterFailures));

        return services;
    }
}
