using System.ComponentModel.DataAnnotations;
using CloudCostObservability.Application.Contracts;
using CloudCostObservability.Domain.Services;
using CloudCostObservability.Infrastructure.Ingestion;
using CloudCostObservability.Infrastructure.Persistence;
using CloudCostObservability.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace CloudCostObservability.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    [Required] public string Provider { get; init; } = "Sqlite";
    [Required] public string ConnectionString { get; init; } = "Data Source=cloud-cost-observability.db";
}

public sealed class CostDataOptions
{
    public const string SectionName = "CostData";
    [Required] public string Provider { get; init; } = "Synthetic";
    public string AzureExportPath { get; init; } = string.Empty;
    public string AwsCurPath { get; init; } = string.Empty;
}

public static class FinOpsInfrastructureExtensions
{
    public static IServiceCollection AddFinOpsInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => string.Equals(options.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase), "Only SQLite is supported by this local reference implementation.")
            .ValidateOnStart();
        services.AddOptions<CostDataOptions>()
            .Bind(configuration.GetSection(CostDataOptions.SectionName))
            .ValidateDataAnnotations()
            .Validate(options => options.Provider.Equals("Synthetic", StringComparison.OrdinalIgnoreCase) ||
                                 options.Provider.Equals("AzureCostManagementExport", StringComparison.OrdinalIgnoreCase) ||
                                 options.Provider.Equals("AwsCur", StringComparison.OrdinalIgnoreCase),
                "CostData:Provider must be Synthetic, AzureCostManagementExport, or AwsCur.")
            .ValidateOnStart();
        services.AddDbContext<FinOpsDbContext>((provider, options) =>
        {
            var database = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseSqlite(database.ConnectionString);
        });
        services.AddSingleton<SyntheticFinOpsData>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<AllocationEngine>();
        services.AddSingleton<TagGovernanceService>();
        services.AddSingleton<BudgetService>();
        services.AddSingleton<ForecastingService>();
        services.AddSingleton<AnomalyDetectionService>();
        services.AddSingleton<RecommendationEngine>();
        services.AddSingleton<UnitEconomicsService>();
        services.AddScoped<IFinOpsService, FinOpsService>();
        return services;
    }
}
