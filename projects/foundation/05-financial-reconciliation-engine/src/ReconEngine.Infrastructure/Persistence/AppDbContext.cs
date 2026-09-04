using Microsoft.EntityFrameworkCore;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence;

/// <summary>
/// The EF Core unit of work. Entity configurations (keys, indexes, unique constraints, value
/// conversions) live in <c>Persistence/Configurations</c> and are applied from the assembly, so the
/// schema is defined once and declaratively.
/// </summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRejection> ImportRejections => Set<ImportRejection>();
    public DbSet<ReconRecord> Records => Set<ReconRecord>();
    public DbSet<MatchingRuleSet> RuleSets => Set<MatchingRuleSet>();
    public DbSet<Match> Matches => Set<Match>();
    public DbSet<MatchEntry> MatchEntries => Set<MatchEntry>();
    public DbSet<ReconciliationRun> Runs => Set<ReconciliationRun>();
    public DbSet<ReconciliationException> Exceptions => Set<ReconciliationException>();
    public DbSet<ExceptionComment> ExceptionComments => Set<ExceptionComment>();
    public DbSet<ExceptionAuditEntry> ExceptionAuditEntries => Set<ExceptionAuditEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Persist every enum as its readable name rather than a magic integer — important for an
        // audit-grade financial store where humans read the rows directly.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.ClrType.GetProperties())
            {
                var underlying = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                if (underlying.IsEnum && entityType.FindProperty(property.Name) is not null)
                    modelBuilder.Entity(entityType.ClrType).Property(property.Name).HasConversion<string>();
            }
        }

        // Every aggregate and child row generates its own Guid identity on the client (see the
        // `= Guid.NewGuid()` initialisers on the entities). Tell EF the store never generates these
        // keys. Without this, a child created during a workflow transition (an ExceptionComment or
        // ExceptionAuditEntry appended to an already-tracked ReconciliationException) is discovered
        // by change tracking with its key *already set*; because the default convention assumes Guid
        // keys are store-generated, EF mistakes it for an existing row and emits an UPDATE instead of
        // an INSERT. That UPDATE matches nothing, so SaveChanges fails the optimistic-concurrency
        // check with "expected to affect 1 row(s), but actually affected 0". Declaring the keys
        // client-generated makes EF track such children as Added (INSERT), as intended.
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var primaryKey = entityType.FindPrimaryKey();
            if (primaryKey is null)
                continue;

            foreach (var keyProperty in primaryKey.Properties)
            {
                if (keyProperty.ClrType == typeof(Guid))
                    modelBuilder.Entity(entityType.ClrType).Property(keyProperty.Name).ValueGeneratedNever();
            }
        }

        base.OnModelCreating(modelBuilder);
    }
}
