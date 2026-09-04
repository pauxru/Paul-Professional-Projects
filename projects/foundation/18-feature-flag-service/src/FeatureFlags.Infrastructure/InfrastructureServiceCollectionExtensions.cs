using System.Text.Json;
using FeatureFlags.Application;
using FeatureFlags.Domain;
using FeatureFlags.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureFlags.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddFeatureFlagInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<FeatureFlagDbContext>(options => options.UseSqlite(connectionString));
        services.AddScoped<IProjectEnvironmentStore, Services.SqliteProjectEnvironmentStore>();
        services.AddScoped<IAuditStore, Services.EfAuditStore>();
        services.AddScoped<IApprovalStore, Services.EfApprovalStore>();
        services.AddScoped<IAnalyticsStore, Services.EfAnalyticsStore>();
        services.AddSingleton<IConfigurationBroadcaster, Services.ChannelConfigurationBroadcaster>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<FlagService>();
        return services;
    }

    public static async Task EnsureDatabaseAndSeedAsync(this IServiceProvider services, bool seedDevelopmentData, CancellationToken cancellationToken = default)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FeatureFlagDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);
        if (!seedDevelopmentData || await db.Projects.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var project = new ProjectEntity { Id = Guid.NewGuid(), Key = "acme", Name = "Acme Manufacturing (fictional)", CreatedAt = now };
        db.Projects.Add(project);
        foreach (var definition in DemoConfiguration.Create(project.Key, now))
        {
            db.Environments.Add(new EnvironmentEntity
            {
                Id = Guid.NewGuid(), ProjectId = project.Id, Key = definition.Key, Name = definition.Name,
                ServerSdkKey = definition.ServerSdkKey, ClientSdkKey = definition.ClientSdkKey,
                Version = definition.Configuration.Version, ConfigurationJson = JsonSerializer.Serialize(definition.Configuration, FeatureFlagJson.Options), UpdatedAt = now
            });
        }
        await db.SaveChangesAsync(cancellationToken);
    }
}

public sealed record DemoEnvironment(string Key, string Name, string ServerSdkKey, string ClientSdkKey, EnvironmentConfiguration Configuration);

public static class DemoConfiguration
{
    public static IReadOnlyList<DemoEnvironment> Create(string projectKey, DateTimeOffset now)
    {
        var segments = new[]
        {
            new SegmentDefinition
            {
                Key = "kenya-users", Name = "Kenya users",
                Rules = [new TargetingRule { VariationIndex = 1, Clauses = [RuleClause.ForEquals("country", "KE")] }]
            }
        };
        var flags = new[]
        {
            new FlagDefinition
            {
                Key = "new-checkout", Name = "New checkout", Description = "Progressive checkout rollout", ValueType = FlagValueType.Boolean,
                ClientSide = true, IsOn = true, OffVariation = 0, FallthroughVariation = 0, Salt = "checkout-v1", LifecycleStatus = FlagLifecycleStatus.Active,
                Variations = [FlagVariation.Create(0, "off", false), FlagVariation.Create(1, "on", true)],
                SegmentTargets = [new SegmentTarget(1, "kenya-users")], Rollout = new PercentageRollout([new WeightedVariation(1, 2_500)])
            },
            new FlagDefinition
            {
                Key = "maintenance-mode", Name = "Maintenance mode", Description = "Emergency kill switch example", ValueType = FlagValueType.Boolean,
                ClientSide = true, IsOn = false, OffVariation = 0, FallthroughVariation = 1, Salt = "maintenance-v1", LifecycleStatus = FlagLifecycleStatus.Active,
                Variations = [FlagVariation.Create(0, "off", false), FlagVariation.Create(1, "on", true)]
            },
            new FlagDefinition
            {
                Key = "pricing-copy", Name = "Pricing copy", Description = "Multivariate copy experiment", ValueType = FlagValueType.String,
                ClientSide = true, IsOn = true, OffVariation = 0, FallthroughVariation = 0, Salt = "pricing-v1", LifecycleStatus = FlagLifecycleStatus.Active,
                Variations = [FlagVariation.Create(0, "control", "Simple pricing"), FlagVariation.Create(1, "challenger", "Flexible plans")],
                Rollout = new PercentageRollout([new WeightedVariation(1, 5_000)])
            },
            new FlagDefinition
            {
                Key = "checkout-timeout-seconds", Name = "Checkout timeout", Description = "Numeric dynamic configuration", ValueType = FlagValueType.Number,
                ClientSide = true, IsOn = true, OffVariation = 0, FallthroughVariation = 1, Salt = "timeout-v1", LifecycleStatus = FlagLifecycleStatus.Active,
                Variations = [FlagVariation.Create(0, "safe", 15), FlagVariation.Create(1, "standard", 30)]
            },
            new FlagDefinition
            {
                Key = "search-config", Name = "Search configuration", Description = "Server-only JSON configuration", ValueType = FlagValueType.Json,
                ClientSide = false, IsOn = true, OffVariation = 0, FallthroughVariation = 1, Salt = "search-v1", LifecycleStatus = FlagLifecycleStatus.New,
                Variations = [FlagVariation.Create(0, "safe", new { maxResults = 10, semantic = false }), FlagVariation.Create(1, "enabled", new { maxResults = 25, semantic = true })]
            }
        };

        return new[]
        {
            CreateEnvironment("dev", "Development", "server-dev-acme-not-a-secret", "client-dev-acme-public-demo", flags, segments, projectKey, now, 25_00),
            CreateEnvironment("staging", "Staging", "server-staging-acme-not-a-secret", "client-staging-acme-public-demo", flags, segments, projectKey, now, 50_00),
            CreateEnvironment("production", "Production", "server-production-acme-not-a-secret", "client-production-acme-public-demo", flags, segments, projectKey, now, 10_00)
        };
    }

    private static DemoEnvironment CreateEnvironment(string key, string name, string serverKey, string clientKey, IReadOnlyList<FlagDefinition> flags, IReadOnlyList<SegmentDefinition> segments, string projectKey, DateTimeOffset now, int checkoutWeightBps)
    {
        var environmentFlags = flags.Select(flag => flag.Key == "new-checkout"
            ? flag with { Rollout = new PercentageRollout([new WeightedVariation(1, checkoutWeightBps)]) }
            : flag).ToArray();
        return new DemoEnvironment(key, name, serverKey, clientKey, new EnvironmentConfiguration
        {
            ProjectKey = projectKey, EnvironmentKey = key, Version = 1, GeneratedAt = now, Flags = environmentFlags, Segments = segments
        });
    }
}
