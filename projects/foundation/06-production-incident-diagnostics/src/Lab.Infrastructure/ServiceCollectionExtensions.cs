using Lab.Application.Abstractions;
using Lab.Application.Contracts;
using Lab.Application.Services;
using Lab.Infrastructure.Persistence;
using Lab.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Lab.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNorthstarInfrastructure(this IServiceCollection services, DatabaseOptions databaseOptions)
    {
        if (!string.Equals(databaseOptions.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("This self-contained lab only supports the Sqlite provider.");
        }

        services.AddDbContext<LogisticsDbContext>(options => options.UseSqlite(databaseOptions.ConnectionString));
        services.AddScoped<IOrderRepository, EfOrderRepository>();
        services.AddScoped<OrderService>();
        services.AddSingleton<IClock, SystemClock>();
        return services;
    }
}
