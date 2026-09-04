using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

using Contoso.Payments.Application.Idempotency;
using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Application.Webhooks;
using Contoso.Payments.Domain.Audit;
using Contoso.Payments.Domain.Catalog;
using Contoso.Payments.Domain.Inventory;
using Contoso.Payments.Domain.Ledger;
using Contoso.Payments.Domain.Orders;
using Contoso.Payments.Domain.Payments;
using Contoso.Payments.Domain.Refunds;
using Contoso.Payments.Application.Reconciliation;

namespace Contoso.Payments.Application.Abstractions;

/// <summary>
/// The write-side DbContext surface exposed to the Application layer.  Concrete implementation
/// lives in Infrastructure so the domain/application can be tested against any provider.
/// </summary>
public interface IAppDbContext
{
    DbSet<Product> Products { get; }
    DbSet<InventoryItem> Inventory { get; }
    DbSet<Order> Orders { get; }
    DbSet<PaymentIntent> PaymentIntents { get; }
    DbSet<Refund> Refunds { get; }
    DbSet<LedgerEntry> LedgerEntries { get; }
    DbSet<AuditEvent> AuditEvents { get; }
    DbSet<IdempotencyRecord> IdempotencyRecords { get; }
    DbSet<OutboxMessage> OutboxMessages { get; }
    DbSet<OutboxDeadLetter> OutboxDeadLetters { get; }
    DbSet<WebhookReplayRecord> WebhookReplays { get; }
    DbSet<ReconciliationRun> ReconciliationRuns { get; }
    DbSet<ReconciliationDiscrepancy> ReconciliationDiscrepancies { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);

    Task<IDbContextTransaction> BeginTransactionAsync(CancellationToken cancellationToken = default);
}
