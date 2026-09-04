using FraudPipeline.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FraudPipeline.Infrastructure.Persistence;

public sealed class FraudDbContext : DbContext
{
    public FraudDbContext(DbContextOptions<FraudDbContext> options) : base(options) { }

    public DbSet<Transaction> Transactions => Set<Transaction>();
    public DbSet<ScoringDecision> Decisions => Set<ScoringDecision>();
    public DbSet<Ruleset> Rulesets => Set<Ruleset>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Case> Cases => Set<Case>();
    public DbSet<CaseNote> CaseNotes => Set<CaseNote>();
    public DbSet<ListEntry> ListEntries => Set<ListEntry>();
    public DbSet<DeadLetterEvent> DeadLetterEvents => Set<DeadLetterEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(FraudDbContext).Assembly);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite can't ORDER BY DateTimeOffset; use a value converter so it's stored as long ticks.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }
}

