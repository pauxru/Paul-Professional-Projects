using System.ComponentModel.DataAnnotations;
using Iiot.Application;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Iiot.Infrastructure;

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    [Required]
    public string Provider { get; init; } = "Sqlite";

    [Required]
    public string ConnectionString { get; init; } = "Data Source=iiot.db";
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIiotInfrastructure(this IServiceCollection services, DatabaseOptions options)
    {
        if (!string.Equals(options.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This reference implementation ships with the SQLite adapter enabled.");
        }

        services.AddDbContext<IiotDbContext>(builder => builder.UseSqlite(options.ConnectionString));
        services.AddScoped<SqliteIiotRepository>();
        services.AddScoped<IDeviceRegistryStore>(provider => provider.GetRequiredService<SqliteIiotRepository>());
        services.AddScoped<ITelemetryStore>(provider => provider.GetRequiredService<SqliteIiotRepository>());
        services.AddScoped<IRuleStore>(provider => provider.GetRequiredService<SqliteIiotRepository>());
        services.AddScoped<IAlertStore>(provider => provider.GetRequiredService<SqliteIiotRepository>());
        services.AddScoped<ICommandStore>(provider => provider.GetRequiredService<SqliteIiotRepository>());
        services.AddSingleton<IDeviceKeyProtector, Pbkdf2DeviceKeyProtector>();
        return services;
    }

    public static async Task EnsureIiotDatabaseAsync(this IServiceProvider provider, CancellationToken cancellationToken = default)
    {
        using var scope = provider.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<IiotDbContext>();
        await database.Database.EnsureCreatedAsync(cancellationToken);
    }
}
