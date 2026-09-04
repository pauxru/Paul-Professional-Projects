using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SavannaLogistics.Application;
using SavannaLogistics.Domain;

namespace SavannaLogistics.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddSavannaInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<DatabaseWriteGate>();
        services.AddDbContext<LogisticsDbContext>((serviceProvider, options) =>
        {
            var database = serviceProvider.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            if (!string.Equals(database.Provider, "Sqlite", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Only the SQLite local adapter is available in this build.");
            }

            options.UseSqlite(database.ConnectionString);
        });
        services.AddScoped<ILogisticsRepository, EfLogisticsRepository>();

        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TelemetryOptions>>().Value;
            return new ExpiringLruDeduplicator(
                options.DedupCacheCapacity,
                TimeSpan.FromSeconds(options.DedupExpirySeconds),
                serviceProvider.GetRequiredService<IClock>());
        });
        services.AddSingleton(serviceProvider =>
        {
            var options = serviceProvider.GetRequiredService<IOptions<TelemetryOptions>>().Value;
            return new WatermarkReorderBuffer(TimeSpan.FromSeconds(options.AllowedLatenessSeconds));
        });
        services.AddSingleton(serviceProvider =>
            new EtaCalculator(serviceProvider.GetRequiredService<IOptions<EtaOptions>>().Value));
        services.AddSingleton(serviceProvider =>
            new AlertRuleEngine(serviceProvider.GetRequiredService<IOptions<AlertOptions>>().Value));
        services.AddSingleton(serviceProvider =>
            new AlertSuppressionWindow(TimeSpan.FromSeconds(
                serviceProvider.GetRequiredService<IOptions<AlertOptions>>().Value.SuppressionWindowSeconds)));
        services.AddSingleton<BoundaryHysteresisTracker>();
        services.AddSingleton<GeofenceSpatialIndex>();
        services.AddSingleton<GeofenceIndexCatalog>();

        services.AddSingleton<TelemetryEventBusHostedService>();
        services.AddSingleton<ITelemetryEventBus>(serviceProvider =>
            serviceProvider.GetRequiredService<TelemetryEventBusHostedService>());
        services.AddSingleton<IHostedService>(serviceProvider =>
            serviceProvider.GetRequiredService<TelemetryEventBusHostedService>());
        services.AddHostedService<WatermarkFlushWorker>();
        services.AddHostedService<OfflineDetectionWorker>();
        services.AddHostedService<GeofenceIndexWarmup>();

        services.AddScoped<WatermarkPublisher>();
        services.AddScoped<ITelemetryIngestion, TelemetryIngestionService>();
        services.AddScoped<OrderedPingProcessor>();
        services.AddScoped<IReplayService, ReplayService>();
        services.AddScoped<ISimulatorService, TelemetrySimulatorService>();
        services.AddScoped<DemoDataSeeder>();
        return services;
    }
}

public sealed class GeofenceIndexWarmup(
    IServiceScopeFactory scopeFactory,
    GeofenceIndexCatalog catalog) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ILogisticsRepository>();
        catalog.Replace(await repository.GetAllGeofencesAsync(cancellationToken));
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class DemoDataSeeder(
    ILogisticsRepository repository,
    IClock clock,
    GeofenceIndexCatalog geofenceCatalog)
{
    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        var existing = await repository.GetVehiclesAsync(1, 1, cancellationToken);
        if (existing.TotalCount > 0)
        {
            geofenceCatalog.Replace(await repository.GetAllGeofencesAsync(cancellationToken));
            return;
        }

        var drivers = new[]
        {
            new Driver(Guid.Parse("10000000-0000-0000-0000-000000000001"), "Amina Demo", "KE-DEMO-001", "synthetic-001"),
            new Driver(Guid.Parse("10000000-0000-0000-0000-000000000002"), "Kamau Demo", "KE-DEMO-002", "synthetic-002"),
            new Driver(Guid.Parse("10000000-0000-0000-0000-000000000003"), "Mwanaidi Demo", "KE-DEMO-003", "synthetic-003")
        };
        foreach (var driver in drivers)
        {
            await repository.AddDriverAsync(driver, cancellationToken);
        }

        var vehicles = new[]
        {
            new Vehicle(Guid.Parse("20000000-0000-0000-0000-000000000001"), "KDL-081A", "Isuzu", "NPR", 4500, 24),
            new Vehicle(Guid.Parse("20000000-0000-0000-0000-000000000002"), "KDM-082B", "Hino", "300", 5200, 28),
            new Vehicle(Guid.Parse("20000000-0000-0000-0000-000000000003"), "KDN-083C", "Fuso", "Canter", 3500, 20)
        };
        for (var index = 0; index < vehicles.Length; index++)
        {
            vehicles[index].AssignDriver(drivers[index].Id);
            await repository.AddVehicleAsync(vehicles[index], cancellationToken);
        }

        var points = new[]
        {
            new GeoPoint(-1.286389, 36.817223),
            new GeoPoint(-1.270700, 36.834100),
            new GeoPoint(-1.248900, 36.871500),
            new GeoPoint(-1.225500, 36.900600),
            new GeoPoint(-1.210700, 36.923100)
        };
        var route = new RoutePlan(
            Guid.Parse("30000000-0000-0000-0000-000000000001"),
            "Nairobi CBD to Embakasi synthetic route",
            points);
        var stops = new[]
        {
            new RouteStop(Guid.Parse("31000000-0000-0000-0000-000000000001"), route.Id, 0, "CBD depot", points[0], 0.18, 2),
            new RouteStop(Guid.Parse("31000000-0000-0000-0000-000000000002"), route.Id, 1, "Industrial Area", points[2], 0.18, 4),
            new RouteStop(Guid.Parse("31000000-0000-0000-0000-000000000003"), route.Id, 2, "Embakasi hub", points[^1], 0.20, 3)
        };
        await repository.AddRouteAsync(route, stops, cancellationToken);

        var geofences = new[]
        {
            Geofence.Circle(
                Guid.Parse("40000000-0000-0000-0000-000000000001"),
                "Savanna Nairobi depot (fictional)",
                points[0],
                0.35),
            Geofence.Polygon(
                Guid.Parse("40000000-0000-0000-0000-000000000002"),
                "Industrial Area service zone",
                [
                    new GeoPoint(-1.263, 36.847),
                    new GeoPoint(-1.238, 36.847),
                    new GeoPoint(-1.238, 36.884),
                    new GeoPoint(-1.263, 36.884)
                ])
        };
        foreach (var geofence in geofences)
        {
            await repository.AddGeofenceAsync(geofence, cancellationToken);
        }

        var trip = new Trip(
            Guid.Parse("50000000-0000-0000-0000-000000000001"),
            vehicles[0].Id,
            route.Id,
            clock.UtcNow,
            clock.UtcNow.AddHours(3));
        await repository.AddTripAsync(trip, cancellationToken);
        trip.Start(clock.UtcNow);
        trip.MarkInTransit();
        await repository.SaveTripAsync(trip, cancellationToken);
        geofenceCatalog.Replace(geofences);
    }
}
