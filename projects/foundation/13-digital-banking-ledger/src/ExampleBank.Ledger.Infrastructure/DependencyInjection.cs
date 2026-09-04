using ExampleBank.Ledger.Application.Abstractions;
using ExampleBank.Ledger.Infrastructure.Concurrency;
using ExampleBank.Ledger.Infrastructure.Fx;
using ExampleBank.Ledger.Infrastructure.Hosting;
using ExampleBank.Ledger.Infrastructure.Persistence;
using ExampleBank.Ledger.Infrastructure.Seeding;
using ExampleBank.Ledger.Infrastructure.Telemetry;
using ExampleBank.Ledger.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ExampleBank.Ledger.Infrastructure;

/// <summary>Registers the infrastructure adapters (persistence, clock, locks, metrics, FX, sweeper).</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddLedgerInfrastructure(
        this IServiceCollection services,
        string connectionString,
        bool addHostedServices = true)
    {
        services.AddDbContextFactory<LedgerDbContext>((sp, options) =>
        {
            // Resolve the connection string from configuration at runtime so integration tests
            // (WebApplicationFactory) can override ConnectionStrings:Ledger after the host is built.
            var runtimeConnectionString =
                sp.GetService<IConfiguration>()?.GetConnectionString("Ledger") ?? connectionString;
            options.UseSqlite(runtimeConnectionString).AddInterceptors(new SqlitePragmaInterceptor());
        });

        services.AddScoped<ILedgerUnitOfWorkFactory, LedgerUnitOfWorkFactory>();

        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IAccountLockManager, AccountLockManager>();
        services.AddSingleton<ILedgerMetrics, LedgerMetrics>();
        services.AddSingleton<IFxRateProvider, SeededFxRateProvider>();

        if (addHostedServices)
        {
            services.AddHostedService<HoldExpiryBackgroundService>();
        }

        return services;
    }

    /// <summary>Ensures the database exists (schema created) and the chart of accounts is seeded.</summary>
    public static async Task InitializeLedgerDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<LedgerDbContext>>();
        var clock = services.GetRequiredService<IClock>();

        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        await LedgerSeeder.SeedAsync(db, clock.UtcNow, cancellationToken);
    }
}
