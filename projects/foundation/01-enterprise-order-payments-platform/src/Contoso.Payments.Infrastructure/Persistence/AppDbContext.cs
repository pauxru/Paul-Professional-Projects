using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Contoso.Payments.Application.Abstractions;
using Contoso.Payments.Application.Idempotency;
using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Application.Reconciliation;
using Contoso.Payments.Application.Webhooks;
using Contoso.Payments.Domain.Audit;
using Contoso.Payments.Domain.Catalog;
using Contoso.Payments.Domain.Inventory;
using Contoso.Payments.Domain.Ledger;
using Contoso.Payments.Domain.Orders;
using Contoso.Payments.Domain.Payments;
using Contoso.Payments.Domain.Refunds;
using Contoso.Payments.Infrastructure.Persistence.Configurations;

namespace Contoso.Payments.Infrastructure.Persistence;

public sealed class AppDbContext : DbContext, IAppDbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Product> Products => Set<Product>();
    public DbSet<InventoryItem> Inventory => Set<InventoryItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<PaymentIntent> PaymentIntents => Set<PaymentIntent>();
    public DbSet<Refund> Refunds => Set<Refund>();
    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<OutboxDeadLetter> OutboxDeadLetters => Set<OutboxDeadLetter>();
    public DbSet<WebhookReplayRecord> WebhookReplays => Set<WebhookReplayRecord>();
    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();
    public DbSet<ReconciliationDiscrepancy> ReconciliationDiscrepancies => Set<ReconciliationDiscrepancy>();

    public async Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default)
    {
        // SQLite in-memory does not support Serializable + explicit BeginTransaction on shared
        // connections; we still model transactions for the sake of the outbox invariant.  Real
        // provider transactions are used everywhere except when the connection is already in one.
        if (Database.CurrentTransaction is not null)
            return Database.CurrentTransaction;
        return await Database.BeginTransactionAsync(cancellationToken);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new ProductConfiguration());
        modelBuilder.ApplyConfiguration(new InventoryItemConfiguration());
        modelBuilder.ApplyConfiguration(new StockReservationConfiguration());
        modelBuilder.ApplyConfiguration(new OrderConfiguration());
        modelBuilder.ApplyConfiguration(new OrderLineConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentIntentConfiguration());
        modelBuilder.ApplyConfiguration(new PaymentAttemptConfiguration());
        modelBuilder.ApplyConfiguration(new RefundConfiguration());
        modelBuilder.ApplyConfiguration(new LedgerEntryConfiguration());
        modelBuilder.ApplyConfiguration(new AuditEventConfiguration());
        modelBuilder.ApplyConfiguration(new IdempotencyRecordConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxDeadLetterConfiguration());
        modelBuilder.ApplyConfiguration(new WebhookReplayConfiguration());
        modelBuilder.ApplyConfiguration(new ReconciliationRunConfiguration());
        modelBuilder.ApplyConfiguration(new ReconciliationDiscrepancyConfiguration());

        // SQLite does not translate DateTimeOffset comparisons natively.  Apply a value
        // converter so ordering and range predicates work uniformly across providers.
        var dtoConverter = new Microsoft.EntityFrameworkCore.Storage.ValueConversion.DateTimeOffsetToBinaryConverter();
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var prop in entity.GetProperties())
            {
                if (prop.ClrType == typeof(DateTimeOffset) || prop.ClrType == typeof(DateTimeOffset?))
                    prop.SetValueConverter(dtoConverter);

                // All Guid primary keys are assigned by the application (via IIdGenerator).
                // If we let EF Core treat them as ValueGeneratedOnAdd, it detects our provided
                // value as a "user-supplied existing PK" and issues an UPDATE instead of INSERT
                // for freshly added child entities in navigation collections.  Marking them
                // ValueGeneratedNever forces EF to trust the add-vs-modify state derived from the
                // change tracker, which is what we want.
                if (prop.IsPrimaryKey() && prop.ClrType == typeof(Guid))
                    prop.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
            }
        }

        base.OnModelCreating(modelBuilder);
    }
}
