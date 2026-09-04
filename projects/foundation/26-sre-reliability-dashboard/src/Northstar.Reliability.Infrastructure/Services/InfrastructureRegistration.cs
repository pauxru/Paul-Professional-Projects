using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Northstar.Reliability.Application.Abstractions;
using Northstar.Reliability.Infrastructure.Persistence;

namespace Northstar.Reliability.Infrastructure.Services;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; set; } = "Sqlite";

    [Required]
    public string ConnectionString { get; set; } = "Data Source=northstar-reliability.db";
}

public static class InfrastructureRegistration
{
    public static IServiceCollection AddReliabilityInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        if (!string.Equals(options.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This reference implementation supports the SQLite adapter only.");
        }

        services.AddDbContext<ReliabilityDbContext>(builder => builder.UseSqlite(options.ConnectionString));
        services.AddScoped<IReliabilityStore, SqliteReliabilityStore>();
        return services;
    }

    public static async Task EnsureDatabaseCreatedAsync(this IServiceProvider provider, CancellationToken cancellationToken = default)
    {
        using var scope = provider.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<ReliabilityDbContext>();
        await database.Database.EnsureCreatedAsync(cancellationToken);
    }
}
