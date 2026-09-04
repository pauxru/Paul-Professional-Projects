using Lab.Diagnostics.Measurement;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Lab.Scenarios.Incidents;

public sealed class NPlusOneScenario : IIncidentScenario
{
    public string Id => "INC-001";

    public string Name => "N+1 EF Core query pattern";

    public async Task<ScenarioReport> RunAsync(ScenarioRunOptions options, CancellationToken cancellationToken)
    {
        var orderCount = options.BoundedRequests(10, 80);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.Timeout);
        var token = budget.Token;
        var session = new MeasurementSession();
        var interceptor = new EfCommandCounterInterceptor();

        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(token);
        var dbOptions = new DbContextOptionsBuilder<NPlusOneContext>()
            .UseSqlite(connection)
            .AddInterceptors(interceptor)
            .Options;

        await using var db = new NPlusOneContext(dbOptions);
        await db.Database.EnsureCreatedAsync(token);

        for (var index = 1; index <= orderCount; index++)
        {
            db.Orders.Add(new ScenarioOrder { Id = index, Reference = $"NS-{index:D4}" });
            db.Shipments.Add(new ScenarioShipment { Id = index, OrderId = index, TrackingCode = $"TRK-{index:D4}" });
        }

        await db.SaveChangesAsync(token);
        interceptor.Reset();
        var returnedShipments = 0;

        if (options.Mode == ScenarioMode.Broken)
        {
            var orders = await db.Orders.AsNoTracking().OrderBy(x => x.Id).ToListAsync(token);
            foreach (var order in orders)
            {
                var shipments = await db.Shipments
                    .AsNoTracking()
                    .Where(x => x.OrderId == order.Id)
                    .ToListAsync(token);
                returnedShipments += shipments.Count;
            }
        }
        else
        {
            var orders = await db.Orders
                .AsNoTracking()
                .OrderBy(x => x.Id)
                .Select(x => new { x.Id, ShipmentCount = x.Shipments.Count })
                .ToListAsync(token);
            returnedShipments = orders.Sum(x => x.ShipmentCount);
        }

        var outcome = session.Complete();
        return new ScenarioReport
        {
            ScenarioId = Id,
            ScenarioName = Name,
            Mode = options.Mode,
            RequestedOperations = orderCount,
            StartedAtUtc = DateTimeOffset.UtcNow,
            ElapsedMilliseconds = outcome.Elapsed.TotalMilliseconds,
            Metrics = new Dictionary<string, object?>
            {
                ["sqlRoundTrips"] = interceptor.Count,
                ["ordersRead"] = orderCount,
                ["shipmentsRead"] = returnedShipments,
                ["roundTripsPerOrder"] = Math.Round((double)interceptor.Count / orderCount, 2),
                ["allocatedBytes"] = outcome.Delta.AllocatedBytes
            },
            Evidence =
            [
                $"EF DbCommandInterceptor captured {interceptor.Count} commands after seeding.",
                $"First SQL command: {interceptor.Samples.FirstOrDefault() ?? "none"}"
            ],
            Limitations =
            [
                "The lab deliberately uses explicit loop loading rather than lazy-loading proxies so the query count is deterministic."
            ]
        };
    }

    private sealed class NPlusOneContext(DbContextOptions<NPlusOneContext> options) : DbContext(options)
    {
        public DbSet<ScenarioOrder> Orders => Set<ScenarioOrder>();

        public DbSet<ScenarioShipment> Shipments => Set<ScenarioShipment>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ScenarioOrder>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.Reference).HasMaxLength(32);
                entity.HasMany(x => x.Shipments).WithOne().HasForeignKey(x => x.OrderId);
            });
            modelBuilder.Entity<ScenarioShipment>(entity =>
            {
                entity.HasKey(x => x.Id);
                entity.Property(x => x.TrackingCode).HasMaxLength(32);
            });
        }
    }

    private sealed class ScenarioOrder
    {
        public int Id { get; init; }

        public string Reference { get; init; } = string.Empty;

        public List<ScenarioShipment> Shipments { get; init; } = [];
    }

    private sealed class ScenarioShipment
    {
        public int Id { get; init; }

        public int OrderId { get; init; }

        public string TrackingCode { get; init; } = string.Empty;
    }
}
