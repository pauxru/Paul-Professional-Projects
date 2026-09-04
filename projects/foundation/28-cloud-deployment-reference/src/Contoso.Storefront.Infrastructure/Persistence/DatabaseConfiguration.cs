using Contoso.Storefront.Application.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Contoso.Storefront.Infrastructure.Persistence;

public static class DatabaseConfiguration
{
    public static DbContextOptionsBuilder UseStorefrontDatabase(
        this DbContextOptionsBuilder builder,
        DatabaseOptions options)
    {
        return options.Provider.Trim().ToLowerInvariant() switch
        {
            "sqlite" => builder.UseSqlite(options.ConnectionString),
            "postgres" or "postgresql" => builder.UseNpgsql(options.ConnectionString),
            "sqlserver" => builder.UseSqlServer(options.ConnectionString),
            _ => throw new InvalidOperationException(
                $"Unsupported database provider '{options.Provider}'. Use Sqlite, Postgres, or SqlServer.")
        };
    }

    public static IServiceCollection AddStorefrontDatabase(this IServiceCollection services)
    {
        services.AddDbContext<StorefrontDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            options.UseStorefrontDatabase(database);
        });
        return services;
    }
}

public sealed class StorefrontDesignTimeDbContextFactory : IDesignTimeDbContextFactory<StorefrontDbContext>
{
    public StorefrontDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<StorefrontDbContext>()
            .UseSqlite("Data Source=storefront-design.db")
            .Options;
        return new StorefrontDbContext(options);
    }
}
