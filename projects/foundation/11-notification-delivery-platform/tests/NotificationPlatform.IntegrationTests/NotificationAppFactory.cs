namespace NotificationPlatform.IntegrationTests;

using System.Collections.Generic;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NotificationPlatform.Application.Abstractions;
using NotificationPlatform.Application.Providers;
using NotificationPlatform.Domain.Common;
using NotificationPlatform.Infrastructure.Persistence;
using NotificationPlatform.Infrastructure.Providers.Common;
using NotificationPlatform.Infrastructure.Seed;

/// <summary>
/// Web application factory that:
///  - runs in the Testing environment (no hosted worker, no seed at startup),
///  - uses a shared in-memory SQLite connection scoped to the factory instance,
///  - swaps IClock for a deterministic FakeClock,
///  - lets tests replace the IChannelProvider set,
///  - re-runs the seed against the isolated DB.
/// </summary>
public sealed class NotificationAppFactory : WebApplicationFactory<Program>
{
    private SqliteConnection? _sharedConn;
    private readonly List<IChannelProvider> _providerOverrides = new();

    public FakeClock Clock { get; } = new FakeClock(new DateTimeOffset(2026, 09, 03, 12, 00, 00, TimeSpan.Zero));

    public void UseProviders(params IChannelProvider[] providers)
    {
        _providerOverrides.Clear();
        _providerOverrides.AddRange(providers);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureServices(services =>
        {
            // Kill previously registered DbContext (both scoped and factory).
            RemoveAll(services, typeof(DbContextOptions<AppDbContext>));
            RemoveAll(services, typeof(IDbContextFactory<AppDbContext>));
            RemoveAll(services, typeof(AppDbContext));

            _sharedConn = new SqliteConnection("DataSource=:memory:");
            _sharedConn.Open();

            services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(_sharedConn));
            services.AddDbContext<AppDbContext>(o => o.UseSqlite(_sharedConn));

            // Deterministic clock.
            RemoveAll(services, typeof(IClock));
            services.AddSingleton<IClock>(Clock);

            if (_providerOverrides.Count > 0)
            {
                RemoveAll(services, typeof(IChannelProvider));
                foreach (var p in _providerOverrides)
                    services.AddSingleton(p);
            }
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _sharedConn?.Dispose();
    }

    public async Task EnsureSeededAsync()
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.EnsureCreatedAsync();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();
        var ids = scope.ServiceProvider.GetRequiredService<IIdGenerator>();
        var lf = scope.ServiceProvider.GetRequiredService<ILoggerFactory>();
        await SeedData.EnsureSeedAsync(db, clock, ids, lf.CreateLogger("Seed"), CancellationToken.None);
    }

    private static void RemoveAll(IServiceCollection services, Type type)
    {
        for (int i = services.Count - 1; i >= 0; i--)
            if (services[i].ServiceType == type) services.RemoveAt(i);
    }
}
