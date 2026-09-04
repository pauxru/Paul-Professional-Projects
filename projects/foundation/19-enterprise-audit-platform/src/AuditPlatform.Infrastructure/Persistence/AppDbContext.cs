using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Integrity;
using AuditPlatform.Domain.Retention;
using AuditPlatform.Domain.Schemas;
using AuditPlatform.Infrastructure.Persistence.Configurations;
using AuditPlatform.Infrastructure.Persistence.Interceptors;
using Microsoft.EntityFrameworkCore;

namespace AuditPlatform.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext
{
    private readonly AppendOnlyInterceptor _interceptor;

    public AppDbContext(DbContextOptions<AppDbContext> options, AppendOnlyInterceptor interceptor) : base(options)
    {
        _interceptor = interceptor;
    }

    public DbSet<AuditEvent> Events => Set<AuditEvent>();
    public DbSet<EventSchema> Schemas => Set<EventSchema>();
    public DbSet<Checkpoint> Checkpoints => Set<Checkpoint>();
    public DbSet<RetentionPolicy> RetentionPolicies => Set<RetentionPolicy>();
    public DbSet<LegalHold> LegalHolds => Set<LegalHold>();
    public DbSet<SavedQuery> SavedQueries => Set<SavedQuery>();
    public DbSet<DeadLetterEvent> DeadLetter => Set<DeadLetterEvent>();
    public DbSet<SearchIndexEntry> SearchIndex => Set<SearchIndexEntry>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.AddInterceptors(_interceptor);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);
        // SQLite's LINQ provider cannot translate DateTimeOffset comparisons directly. Store as
        // Unix milliseconds so ordering, ranges, and equality all fold into simple integer SQL.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToLongConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new AuditEventConfiguration());
        modelBuilder.ApplyConfiguration(new EventSchemaConfiguration());
        modelBuilder.ApplyConfiguration(new CheckpointConfiguration());
        modelBuilder.ApplyConfiguration(new RetentionPolicyConfiguration());
        modelBuilder.ApplyConfiguration(new LegalHoldConfiguration());
        modelBuilder.ApplyConfiguration(new SavedQueryConfiguration());
        modelBuilder.ApplyConfiguration(new DeadLetterEventConfiguration());
        modelBuilder.ApplyConfiguration(new SearchIndexEntryConfiguration());
    }
}

public sealed class SearchIndexEntry
{
    public long Id { get; set; }
    public Guid EventId { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

internal sealed class DateTimeOffsetToLongConverter : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTimeOffset, long>
{
    public DateTimeOffsetToLongConverter()
        : base(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v))
    {
    }
}
