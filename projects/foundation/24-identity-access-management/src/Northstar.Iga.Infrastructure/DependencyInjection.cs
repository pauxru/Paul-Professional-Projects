using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Northstar.Iga.Application;

namespace Northstar.Iga.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIgaInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<LifecycleOptions>()
            .Bind(configuration.GetSection(LifecycleOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<GovernanceOptions>()
            .Bind(configuration.GetSection(GovernanceOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<ProvisioningOptions>()
            .Bind(configuration.GetSection(ProvisioningOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();
        services.AddOptions<CorsOptions>()
            .Bind(configuration.GetSection(CorsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddDbContext<IgaDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            if (!database.Provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Database provider '{database.Provider}' is not available in this offline reference implementation.");
            }

            options.UseSqlite(database.ConnectionString);
        });

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAuditContextAccessor, NullAuditContextAccessor>();
        services.AddScoped<IIgaService, IgaService>();
        services.AddScoped<IDatabaseInitializer, DatabaseInitializer>();
        services.AddSingleton<SimulatedConnectorState>();
        services.AddSingleton<SimulatedConnectorControl>();
        services.AddSingleton<ISimulatedConnectorControl>(x => x.GetRequiredService<SimulatedConnectorControl>());
        services.AddSingleton<IProvisioningConnector>(x => new SimulatedProvisioningConnector(
            "finance",
            x.GetRequiredService<SimulatedConnectorState>(),
            x.GetRequiredService<SimulatedConnectorControl>()));
        services.AddSingleton<IProvisioningConnector>(x => new SimulatedProvisioningConnector(
            "erp",
            x.GetRequiredService<SimulatedConnectorState>(),
            x.GetRequiredService<SimulatedConnectorControl>()));
        services.AddSingleton<IProvisioningConnector>(x => new SimulatedProvisioningConnector(
            "crm",
            x.GetRequiredService<SimulatedConnectorState>(),
            x.GetRequiredService<SimulatedConnectorControl>()));
        services.AddSingleton<IHrIdentitySource, SimulatedHrIdentitySource>();
        return services;
    }
}

public interface IDatabaseInitializer
{
    Task InitializeAsync(bool seedDemoData, CancellationToken cancellationToken);
}
