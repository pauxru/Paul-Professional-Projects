using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SavannaLogistics.Application;
using SavannaLogistics.Domain;

namespace SavannaLogistics.Infrastructure;

public sealed class LogisticsDbContext(DbContextOptions<LogisticsDbContext> options) : DbContext(options)
{
    public DbSet<Vehicle> Vehicles => Set<Vehicle>();
    public DbSet<Driver> Drivers => Set<Driver>();
    public DbSet<VehiclePing> VehiclePings => Set<VehiclePing>();
    public DbSet<RoutePlan> Routes => Set<RoutePlan>();
    public DbSet<RouteStop> RouteStops => Set<RouteStop>();
    public DbSet<Trip> Trips => Set<Trip>();
    public DbSet<Geofence> Geofences => Set<Geofence>();
    public DbSet<VehicleState> VehicleStates => Set<VehicleState>();
    public DbSet<AlertRecord> Alerts => Set<AlertRecord>();
    public DbSet<EtaPrediction> EtaPredictions => Set<EtaPrediction>();
    public DbSet<LateArrival> LateArrivals => Set<LateArrival>();
    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LogisticsDbContext).Assembly);
    }
}

public sealed class VehicleConfiguration : IEntityTypeConfiguration<Vehicle>
{
    public void Configure(EntityTypeBuilder<Vehicle> builder)
    {
        builder.ToTable("Vehicles");
        builder.HasKey(vehicle => vehicle.Id);
        builder.Property(vehicle => vehicle.Registration).HasMaxLength(32).IsRequired();
        builder.Property(vehicle => vehicle.Make).HasMaxLength(80).IsRequired();
        builder.Property(vehicle => vehicle.Model).HasMaxLength(80).IsRequired();
        builder.HasIndex(vehicle => vehicle.Registration).IsUnique();
        builder.HasIndex(vehicle => vehicle.Status);
        builder.HasOne<Driver>()
            .WithMany()
            .HasForeignKey(vehicle => vehicle.AssignedDriverId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class DriverConfiguration : IEntityTypeConfiguration<Driver>
{
    public void Configure(EntityTypeBuilder<Driver> builder)
    {
        builder.ToTable("Drivers");
        builder.HasKey(driver => driver.Id);
        builder.Property(driver => driver.Name).HasMaxLength(120).IsRequired();
        builder.Property(driver => driver.LicenceNumber).HasMaxLength(64).IsRequired();
        builder.Property(driver => driver.PhoneAlias).HasMaxLength(64);
        builder.HasIndex(driver => driver.LicenceNumber).IsUnique();
    }
}

public sealed class VehiclePingConfiguration : IEntityTypeConfiguration<VehiclePing>
{
    public void Configure(EntityTypeBuilder<VehiclePing> builder)
    {
        builder.ToTable("VehiclePings");
        builder.HasKey(ping => ping.Id);
        builder.Property(ping => ping.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(ping => ping.DeviceTimestamp)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(ping => ping.IngestTimestamp)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(ping => new { ping.VehicleId, ping.SequenceNumber }).IsUnique();
        builder.HasIndex(ping => ping.DeviceTimestamp);
        builder.HasIndex(ping => new { ping.VehicleId, ping.DeviceTimestamp });
        builder.HasOne<Vehicle>()
            .WithMany()
            .HasForeignKey(ping => ping.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RouteConfiguration : IEntityTypeConfiguration<RoutePlan>
{
    public void Configure(EntityTypeBuilder<RoutePlan> builder)
    {
        builder.ToTable("Routes");
        builder.HasKey(route => route.Id);
        builder.Property(route => route.Name).HasMaxLength(160).IsRequired();
        builder.Property(route => route.PolylineJson).IsRequired();
        builder.HasIndex(route => route.Name);
    }
}

public sealed class RouteStopConfiguration : IEntityTypeConfiguration<RouteStop>
{
    public void Configure(EntityTypeBuilder<RouteStop> builder)
    {
        builder.ToTable("RouteStops");
        builder.HasKey(stop => stop.Id);
        builder.Property(stop => stop.Name).HasMaxLength(160).IsRequired();
        builder.HasIndex(stop => new { stop.RouteId, stop.Sequence }).IsUnique();
        builder.HasOne<RoutePlan>()
            .WithMany()
            .HasForeignKey(stop => stop.RouteId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class TripConfiguration : IEntityTypeConfiguration<Trip>
{
    public void Configure(EntityTypeBuilder<Trip> builder)
    {
        builder.ToTable("Trips");
        builder.HasKey(trip => trip.Id);
        builder.Property(trip => trip.PlannedStart)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(trip => trip.SlaDueAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(trip => trip.Version).IsConcurrencyToken();
        builder.HasIndex(trip => new { trip.VehicleId, trip.Status });
        builder.HasIndex(trip => trip.RouteId);
        builder.HasOne<Vehicle>()
            .WithMany()
            .HasForeignKey(trip => trip.VehicleId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<RoutePlan>()
            .WithMany()
            .HasForeignKey(trip => trip.RouteId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class GeofenceConfiguration : IEntityTypeConfiguration<Geofence>
{
    public void Configure(EntityTypeBuilder<Geofence> builder)
    {
        builder.ToTable("Geofences");
        builder.HasKey(geofence => geofence.Id);
        builder.Property(geofence => geofence.Name).HasMaxLength(160).IsRequired();
        builder.HasIndex(geofence => geofence.Name);
    }
}

public sealed class VehicleStateConfiguration : IEntityTypeConfiguration<VehicleState>
{
    public void Configure(EntityTypeBuilder<VehicleState> builder)
    {
        builder.ToTable("VehicleStates");
        builder.HasKey(state => state.VehicleId);
        builder.Property(state => state.LastSeenAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(state => state.LastSeenAt);
        builder.HasIndex(state => state.Status);
        builder.HasOne<Vehicle>()
            .WithOne()
            .HasForeignKey<VehicleState>(state => state.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class AlertConfiguration : IEntityTypeConfiguration<AlertRecord>
{
    public void Configure(EntityTypeBuilder<AlertRecord> builder)
    {
        builder.ToTable("Alerts");
        builder.HasKey(alert => alert.Id);
        builder.Property(alert => alert.Message).HasMaxLength(500).IsRequired();
        builder.Property(alert => alert.Fingerprint).HasMaxLength(220).IsRequired();
        builder.Property(alert => alert.OccurredAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(alert => alert.Fingerprint).IsUnique();
        builder.HasIndex(alert => new { alert.VehicleId, alert.OccurredAt });
        builder.HasIndex(alert => new { alert.Type, alert.AcknowledgedAt });
        builder.HasOne<Vehicle>()
            .WithMany()
            .HasForeignKey(alert => alert.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Trip>()
            .WithMany()
            .HasForeignKey(alert => alert.TripId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public sealed class EtaPredictionConfiguration : IEntityTypeConfiguration<EtaPrediction>
{
    public void Configure(EntityTypeBuilder<EtaPrediction> builder)
    {
        builder.ToTable("EtaPredictions");
        builder.HasKey(prediction => prediction.Id);
        builder.Property(prediction => prediction.CalculatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.Property(prediction => prediction.PredictedArrival)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(prediction => new { prediction.VehicleId, prediction.CalculatedAt });
        builder.HasIndex(prediction => new { prediction.TripId, prediction.TargetStopId });
        builder.HasOne<Vehicle>()
            .WithMany()
            .HasForeignKey(prediction => prediction.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Trip>()
            .WithMany()
            .HasForeignKey(prediction => prediction.TripId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<RouteStop>()
            .WithMany()
            .HasForeignKey(prediction => prediction.TargetStopId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class LateArrivalConfiguration : IEntityTypeConfiguration<LateArrival>
{
    public void Configure(EntityTypeBuilder<LateArrival> builder)
    {
        builder.ToTable("LateArrivals");
        builder.HasKey(arrival => arrival.Id);
        builder.Property(arrival => arrival.Reason).HasMaxLength(500).IsRequired();
        builder.Property(arrival => arrival.ReceivedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(arrival => new { arrival.VehicleId, arrival.SequenceNumber });
        builder.HasOne<Vehicle>()
            .WithMany()
            .HasForeignKey(arrival => arrival.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<VehiclePing>()
            .WithMany()
            .HasForeignKey(arrival => arrival.PingId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DeadLetterConfiguration : IEntityTypeConfiguration<DeadLetter>
{
    public void Configure(EntityTypeBuilder<DeadLetter> builder)
    {
        builder.ToTable("DeadLetters");
        builder.HasKey(letter => letter.Id);
        builder.Property(letter => letter.Reason).HasMaxLength(2000).IsRequired();
        builder.Property(letter => letter.FailedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        builder.HasIndex(letter => letter.FailedAt);
        builder.HasOne<Vehicle>()
            .WithMany()
            .HasForeignKey(letter => letter.VehicleId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class DatabaseWriteGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

public sealed class EfLogisticsRepository(
    LogisticsDbContext dbContext,
    DatabaseWriteGate writeGate) : ILogisticsRepository
{
    public async Task<PagedResult<Vehicle>> GetVehiclesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Vehicles.AsNoTracking().OrderBy(vehicle => vehicle.Registration);
        return await PageAsync(query, page, pageSize, cancellationToken);
    }

    public Task<Vehicle?> GetVehicleAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Vehicles.SingleOrDefaultAsync(vehicle => vehicle.Id == id, cancellationToken);

    public async Task<IReadOnlySet<Guid>> GetExistingVehicleIdsAsync(
        IReadOnlyCollection<Guid> ids,
        CancellationToken cancellationToken)
    {
        if (ids.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var values = ids.ToArray();
        return (await dbContext.Vehicles.AsNoTracking()
                .Where(vehicle => values.Contains(vehicle.Id))
                .Select(vehicle => vehicle.Id)
                .ToListAsync(cancellationToken))
            .ToHashSet();
    }

    public async Task AddVehicleAsync(Vehicle vehicle, CancellationToken cancellationToken)
    {
        dbContext.Vehicles.Add(vehicle);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<Driver>> GetDriversAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Drivers.AsNoTracking().OrderBy(driver => driver.Name);
        return await PageAsync(query, page, pageSize, cancellationToken);
    }

    public Task<Driver?> GetDriverAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Drivers.SingleOrDefaultAsync(driver => driver.Id == id, cancellationToken);

    public async Task AddDriverAsync(Driver driver, CancellationToken cancellationToken)
    {
        dbContext.Drivers.Add(driver);
        await SaveChangesAsync(cancellationToken);
    }

    public Task SaveVehicleAsync(Vehicle vehicle, CancellationToken cancellationToken) => SaveChangesAsync(cancellationToken);

    public async Task<PagedResult<RoutePlan>> GetRoutesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Routes.AsNoTracking().OrderBy(route => route.Name);
        return await PageAsync(query, page, pageSize, cancellationToken);
    }

    public Task<RoutePlan?> GetRouteAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Routes.AsNoTracking().SingleOrDefaultAsync(route => route.Id == id, cancellationToken);

    public async Task<IReadOnlyList<RouteStop>> GetRouteStopsAsync(Guid routeId, CancellationToken cancellationToken) =>
        await dbContext.RouteStops.AsNoTracking()
            .Where(stop => stop.RouteId == routeId)
            .OrderBy(stop => stop.Sequence)
            .ToListAsync(cancellationToken);

    public async Task AddRouteAsync(RoutePlan route, IReadOnlyList<RouteStop> stops, CancellationToken cancellationToken)
    {
        dbContext.Routes.Add(route);
        dbContext.RouteStops.AddRange(stops);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<Geofence>> GetGeofencesAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Geofences.AsNoTracking().OrderBy(geofence => geofence.Name);
        return await PageAsync(query, page, pageSize, cancellationToken);
    }

    public async Task<IReadOnlyList<Geofence>> GetAllGeofencesAsync(CancellationToken cancellationToken) =>
        await dbContext.Geofences.AsNoTracking().ToListAsync(cancellationToken);

    public async Task AddGeofenceAsync(Geofence geofence, CancellationToken cancellationToken)
    {
        dbContext.Geofences.Add(geofence);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task<PagedResult<Trip>> GetTripsAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Trips.AsNoTracking().OrderByDescending(trip => trip.PlannedStart);
        return await PageAsync(query, page, pageSize, cancellationToken);
    }

    public Task<Trip?> GetTripAsync(Guid id, CancellationToken cancellationToken) =>
        dbContext.Trips.SingleOrDefaultAsync(trip => trip.Id == id, cancellationToken);

    public Task<Trip?> GetActiveTripForVehicleAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        dbContext.Trips.SingleOrDefaultAsync(
            trip => trip.VehicleId == vehicleId &&
                    trip.Status != TripStatus.Completed &&
                    trip.Status != TripStatus.Aborted &&
                    trip.Status != TripStatus.Planned,
            cancellationToken);

    public async Task AddTripAsync(Trip trip, CancellationToken cancellationToken)
    {
        dbContext.Trips.Add(trip);
        await SaveChangesAsync(cancellationToken);
    }

    public Task SaveTripAsync(Trip trip, CancellationToken cancellationToken) => SaveChangesAsync(cancellationToken);

    public async Task<PingPersistenceResult> TryAddPingAsync(VehiclePing ping, CancellationToken cancellationToken) =>
        (await TryAddPingsAsync([ping], cancellationToken))[0];

    public async Task<IReadOnlyList<PingPersistenceResult>> TryAddPingsAsync(
        IReadOnlyList<VehiclePing> pings,
        CancellationToken cancellationToken)
    {
        if (pings.Count == 0)
        {
            return [];
        }

        await writeGate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            var existingByKey = new Dictionary<(Guid VehicleId, long Sequence), VehiclePing>();
            foreach (var chunk in pings.Chunk(500))
            {
                var vehicleIds = chunk.Select(ping => ping.VehicleId).Distinct().ToArray();
                var sequences = chunk.Select(ping => ping.SequenceNumber).Distinct().ToArray();
                var existing = await dbContext.VehiclePings.AsNoTracking()
                    .Where(candidate =>
                        vehicleIds.Contains(candidate.VehicleId) &&
                        sequences.Contains(candidate.SequenceNumber))
                    .ToListAsync(cancellationToken);
                foreach (var stored in existing)
                {
                    existingByKey[(stored.VehicleId, stored.SequenceNumber)] = stored;
                }
            }

            var results = new PingPersistenceResult[pings.Count];
            var additions = new List<VehiclePing>();
            for (var index = 0; index < pings.Count; index++)
            {
                var ping = pings[index];
                if (existingByKey.TryGetValue((ping.VehicleId, ping.SequenceNumber), out var existing))
                {
                    results[index] = existing.ContentHash == ping.ContentHash
                        ? PingPersistenceResult.Duplicate
                        : PingPersistenceResult.Conflict;
                    continue;
                }

                results[index] = PingPersistenceResult.Inserted;
                additions.Add(ping);
            }

            if (additions.Count > 0)
            {
                dbContext.VehiclePings.AddRange(additions);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            return results;
        }
        finally
        {
            writeGate.Semaphore.Release();
        }
    }

    public async Task<IReadOnlyList<VehiclePing>> GetPingsAsync(
        DateTimeOffset from,
        DateTimeOffset to,
        Guid? vehicleId,
        CancellationToken cancellationToken)
    {
        var query = dbContext.VehiclePings.AsNoTracking();
        if (vehicleId.HasValue)
        {
            query = query.Where(ping => ping.VehicleId == vehicleId.Value);
        }

        var candidates = await query.ToListAsync(cancellationToken);
        return candidates
            .Where(ping => ping.DeviceTimestamp >= from && ping.DeviceTimestamp <= to)
            .OrderBy(ping => ping.DeviceTimestamp)
            .ThenBy(ping => ping.SequenceNumber)
            .ToArray();
    }

    public Task<int> CountPingsAsync(CancellationToken cancellationToken) =>
        dbContext.VehiclePings.CountAsync(cancellationToken);

    public async Task AddLateArrivalAsync(LateArrival lateArrival, CancellationToken cancellationToken)
    {
        dbContext.LateArrivals.Add(lateArrival);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task AddDeadLetterAsync(DeadLetter deadLetter, CancellationToken cancellationToken)
    {
        dbContext.DeadLetters.Add(deadLetter);
        await SaveChangesAsync(cancellationToken);
    }

    public Task<int> CountLateArrivalsAsync(CancellationToken cancellationToken) =>
        dbContext.LateArrivals.CountAsync(cancellationToken);

    public Task<int> CountDeadLettersAsync(CancellationToken cancellationToken) =>
        dbContext.DeadLetters.CountAsync(cancellationToken);

    public Task<VehicleState?> GetVehicleStateAsync(Guid vehicleId, CancellationToken cancellationToken) =>
        dbContext.VehicleStates.SingleOrDefaultAsync(state => state.VehicleId == vehicleId, cancellationToken);

    public async Task<IReadOnlyList<VehicleState>> GetVehicleStatesAsync(CancellationToken cancellationToken) =>
        await dbContext.VehicleStates.AsNoTracking().OrderBy(state => state.VehicleId).ToListAsync(cancellationToken);

    public async Task UpsertVehicleStateAsync(VehicleState state, CancellationToken cancellationToken)
    {
        if (dbContext.Entry(state).State == EntityState.Detached)
        {
            var exists = await dbContext.VehicleStates.AsNoTracking()
                .AnyAsync(existing => existing.VehicleId == state.VehicleId, cancellationToken);
            if (exists)
            {
                dbContext.VehicleStates.Update(state);
            }
            else
            {
                dbContext.VehicleStates.Add(state);
            }
        }

        await SaveChangesAsync(cancellationToken);
    }

    public async Task ClearVehicleStatesAsync(CancellationToken cancellationToken)
    {
        await writeGate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await dbContext.VehicleStates.ExecuteDeleteAsync(cancellationToken);
        }
        finally
        {
            writeGate.Semaphore.Release();
        }
    }

    public async Task<bool> TryAddAlertAsync(AlertRecord alert, CancellationToken cancellationToken)
    {
        await writeGate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            if (await dbContext.Alerts.AnyAsync(existing => existing.Fingerprint == alert.Fingerprint, cancellationToken))
            {
                return false;
            }

            dbContext.Alerts.Add(alert);
            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateException)
            {
                dbContext.Entry(alert).State = EntityState.Detached;
                return false;
            }
        }
        finally
        {
            writeGate.Semaphore.Release();
        }
    }

    public async Task<PagedResult<AlertRecord>> GetAlertsAsync(int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = dbContext.Alerts.AsNoTracking().OrderByDescending(alert => alert.OccurredAt);
        return await PageAsync(query, page, pageSize, cancellationToken);
    }

    public Task<int> CountAlertsAsync(CancellationToken cancellationToken) =>
        dbContext.Alerts.CountAsync(cancellationToken);

    public async Task AddEtaPredictionAsync(EtaPrediction prediction, CancellationToken cancellationToken)
    {
        dbContext.EtaPredictions.Add(prediction);
        await SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EtaPrediction>> GetEtaHistoryAsync(
        Guid vehicleId,
        int take,
        CancellationToken cancellationToken) =>
        await dbContext.EtaPredictions.AsNoTracking()
            .Where(prediction => prediction.VehicleId == vehicleId)
            .OrderByDescending(prediction => prediction.CalculatedAt)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);

    public async Task RecordArrivalAccuracyAsync(
        Guid tripId,
        Guid stopId,
        DateTimeOffset actualArrival,
        CancellationToken cancellationToken)
    {
        var predictions = await dbContext.EtaPredictions
            .Where(prediction => prediction.TripId == tripId &&
                                 prediction.TargetStopId == stopId &&
                                 prediction.ActualArrival == null)
            .ToListAsync(cancellationToken);
        foreach (var prediction in predictions)
        {
            prediction.RecordActualArrival(actualArrival);
        }

        if (predictions.Count > 0)
        {
            await SaveChangesAsync(cancellationToken);
        }
    }

    public async Task<(int Samples, double MeanAbsoluteErrorSeconds, double P90AbsoluteErrorSeconds)> GetEtaAccuracyAsync(
        CancellationToken cancellationToken)
    {
        var values = await dbContext.EtaPredictions.AsNoTracking()
            .Where(prediction => prediction.AbsoluteErrorSeconds != null)
            .Select(prediction => prediction.AbsoluteErrorSeconds!.Value)
            .ToListAsync(cancellationToken);
        if (values.Count == 0)
        {
            return (0, 0, 0);
        }

        values.Sort();
        var p90Index = Math.Clamp((int)Math.Ceiling(values.Count * 0.9) - 1, 0, values.Count - 1);
        return (values.Count, values.Average(), values[p90Index]);
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        await writeGate.Semaphore.WaitAsync(cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            writeGate.Semaphore.Release();
        }
    }

    private static async Task<PagedResult<T>> PageAsync<T>(
        IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, page, pageSize, total);
    }
}
