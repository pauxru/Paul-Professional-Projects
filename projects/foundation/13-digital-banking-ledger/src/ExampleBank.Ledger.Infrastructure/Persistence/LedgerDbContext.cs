using ExampleBank.Ledger.Application.Common;
using ExampleBank.Ledger.Domain.Accounts;
using ExampleBank.Ledger.Domain.Fees;
using ExampleBank.Ledger.Domain.Holds;
using ExampleBank.Ledger.Domain.Interest;
using ExampleBank.Ledger.Domain.Journal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ExampleBank.Ledger.Infrastructure.Persistence;

/// <summary>
/// The EF Core context for the ledger. It enforces the append-only invariant in
/// <see cref="SaveChangesAsync(CancellationToken)"/>: once persisted, journal entries and postings
/// may never be modified or deleted — corrections are made by appending reversal entries.
/// </summary>
public sealed class LedgerDbContext : DbContext
{
    public LedgerDbContext(DbContextOptions<LedgerDbContext> options) : base(options)
    {
    }

    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<JournalEntry> JournalEntries => Set<JournalEntry>();
    public DbSet<Posting> Postings => Set<Posting>();
    public DbSet<Hold> Holds => Set<Hold>();
    public DbSet<FeeSchedule> FeeSchedules => Set<FeeSchedule>();
    public DbSet<FeeTier> FeeTiers => Set<FeeTier>();
    public DbSet<InterestAccrual> InterestAccruals => Set<InterestAccrual>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(LedgerDbContext).Assembly);
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        EnforceAppendOnly();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        EnforceAppendOnly();
        return base.SaveChanges();
    }

    /// <summary>Rejects any attempt to update or delete a persisted journal entry or posting.</summary>
    private void EnforceAppendOnly()
    {
        foreach (EntityEntry entry in ChangeTracker.Entries())
        {
            bool isLedgerRecord = entry.Entity is JournalEntry or Posting;
            if (!isLedgerRecord)
            {
                continue;
            }

            if (entry.State is EntityState.Modified or EntityState.Deleted)
            {
                throw new Domain.Common.AppendOnlyViolationException(
                    $"The ledger is append-only; {entry.Entity.GetType().Name} cannot be {entry.State}.");
            }
        }
    }
}
