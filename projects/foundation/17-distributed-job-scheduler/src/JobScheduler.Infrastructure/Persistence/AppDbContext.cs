using JobScheduler.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace JobScheduler.Infrastructure.Persistence;

/// <summary>
/// Converts <see cref="DateTimeOffset"/> to UTC ticks for storage. SQLite has no native
/// <see cref="DateTimeOffset"/> type; storing a 64-bit integer keeps range/ordering comparisons
/// translatable to SQL and monotonic — essential for the due-run and expired-lease scans.
/// </summary>
public sealed class UtcTicksDateTimeOffsetConverter() : ValueConverter<DateTimeOffset, long>(
    v => v.UtcTicks,
    v => new DateTimeOffset(v, TimeSpan.Zero));

/// <summary>
/// EF Core context for the scheduler. Configuration lives in <c>IEntityTypeConfiguration</c>
/// classes; the indexes that make claiming efficient are defined there and documented in
/// <c>docs/database-schema.md</c>.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<JobDefinition> JobDefinitions => Set<JobDefinition>();
    public DbSet<JobRun> JobRuns => Set<JobRun>();
    public DbSet<WorkerNode> WorkerNodes => Set<WorkerNode>();
    public DbSet<LeaderLease> LeaderLeases => Set<LeaderLease>();
    public DbSet<DeadLetterEntry> DeadLetters => Set<DeadLetterEntry>();
    public DbSet<RunLog> RunLogs => Set<RunLog>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Applies to both DateTimeOffset and DateTimeOffset? properties.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksDateTimeOffsetConverter>();
        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
