using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using SubscriptionBilling.Domain;

namespace SubscriptionBilling.Infrastructure;

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<ProductEntity> Products => Set<ProductEntity>();
    public DbSet<PlanEntity> Plans => Set<PlanEntity>();
    public DbSet<PlanVersionEntity> PlanVersions => Set<PlanVersionEntity>();
    public DbSet<MeterEntity> Meters => Set<MeterEntity>();
    public DbSet<CustomerEntity> Customers => Set<CustomerEntity>();
    public DbSet<SubscriptionEntity> Subscriptions => Set<SubscriptionEntity>();
    public DbSet<SubscriptionChangeEntity> SubscriptionChanges => Set<SubscriptionChangeEntity>();
    public DbSet<PendingChargeEntity> PendingCharges => Set<PendingChargeEntity>();
    public DbSet<UsageEventEntity> UsageEvents => Set<UsageEventEntity>();
    public DbSet<UsageRollupEntity> UsageRollups => Set<UsageRollupEntity>();
    public DbSet<UsageUniqueKeyEntity> UsageUniqueKeys => Set<UsageUniqueKeyEntity>();
    public DbSet<InvoiceEntity> Invoices => Set<InvoiceEntity>();
    public DbSet<InvoiceLineEntity> InvoiceLines => Set<InvoiceLineEntity>();
    public DbSet<CouponEntity> Coupons => Set<CouponEntity>();
    public DbSet<CouponRedemptionEntity> CouponRedemptions => Set<CouponRedemptionEntity>();
    public DbSet<CreditEntity> Credits => Set<CreditEntity>();
    public DbSet<CreditApplicationEntity> CreditApplications => Set<CreditApplicationEntity>();
    public DbSet<CreditNoteEntity> CreditNotes => Set<CreditNoteEntity>();
    public DbSet<PaymentAttemptEntity> PaymentAttempts => Set<PaymentAttemptEntity>();
    public DbSet<DunningCaseEntity> DunningCases => Set<DunningCaseEntity>();
    public DbSet<WebhookNonceEntity> WebhookNonces => Set<WebhookNonceEntity>();
    public DbSet<OutboundWebhookEntity> OutboundWebhooks => Set<OutboundWebhookEntity>();
    public DbSet<IdempotencyRecordEntity> IdempotencyRecords => Set<IdempotencyRecordEntity>();
    public DbSet<AuditRecordEntity> AuditRecords => Set<AuditRecordEntity>();
    public DbSet<SequenceEntity> Sequences => Set<SequenceEntity>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcDateTimeOffsetConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BillingDbContext).Assembly);
    }

    public sealed class UtcDateTimeOffsetConverter()
        : ValueConverter<DateTimeOffset, long>(
            value => value.UtcTicks,
            value => new DateTimeOffset(value, TimeSpan.Zero));

    public override int SaveChanges()
    {
        EnforceAppendOnlyAndInvoiceImmutability();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(
        CancellationToken cancellationToken = default)
    {
        EnforceAppendOnlyAndInvoiceImmutability();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void EnforceAppendOnlyAndInvoiceImmutability()
    {
        var immutableTypes = new[]
        {
            typeof(PlanVersionEntity),
            typeof(UsageEventEntity),
            typeof(AuditRecordEntity),
            typeof(CreditNoteEntity)
        };
        foreach (var entry in ChangeTracker.Entries()
                     .Where(entry => immutableTypes.Contains(entry.Metadata.ClrType) &&
                                     entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} records are append-only.");
        }

        if (ChangeTracker.Entries<InvoiceLineEntity>()
            .Any(entry => entry.State is EntityState.Modified or EntityState.Deleted))
        {
            throw new InvalidOperationException("Invoice line items are immutable after creation.");
        }

        foreach (var entry in ChangeTracker.Entries<InvoiceEntity>()
                     .Where(entry => entry.State == EntityState.Modified))
        {
            var originalStatus = entry.OriginalValues.GetValue<InvoiceStatus>(
                nameof(InvoiceEntity.Status));
            if (originalStatus == InvoiceStatus.Draft)
            {
                continue;
            }

            var changedProperties = entry.Properties
                .Where(property => property.IsModified)
                .Select(property => property.Metadata.Name)
                .ToArray();
            if (changedProperties.Any(name => name != nameof(InvoiceEntity.Status)))
            {
                throw new InvalidOperationException("Finalized invoice financial fields are immutable.");
            }
        }
    }

}

public sealed class ProductConfiguration : IEntityTypeConfiguration<ProductEntity>
{
    public void Configure(EntityTypeBuilder<ProductEntity> builder)
    {
        builder.ToTable("products");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(160).IsRequired();
        builder.Property(item => item.Description).HasMaxLength(2000);
        builder.HasIndex(item => item.Name).IsUnique();
    }
}

public sealed class PlanConfiguration : IEntityTypeConfiguration<PlanEntity>
{
    public void Configure(EntityTypeBuilder<PlanEntity> builder)
    {
        builder.ToTable("plans");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(160).IsRequired();
        builder.Property(item => item.IntervalUnit).HasConversion<string>();
        builder.HasIndex(item => new { item.ProductId, item.Name }).IsUnique();
        builder.HasOne<ProductEntity>()
            .WithMany()
            .HasForeignKey(item => item.ProductId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<MeterEntity>()
            .WithMany()
            .HasForeignKey(item => item.MeterId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class PlanVersionConfiguration : IEntityTypeConfiguration<PlanVersionEntity>
{
    public void Configure(EntityTypeBuilder<PlanVersionEntity> builder)
    {
        builder.ToTable("plan_versions");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Currency).HasMaxLength(3).IsRequired();
        builder.Property(item => item.PricingJson).IsRequired();
        builder.HasIndex(item => new { item.PlanId, item.Version }).IsUnique();
        builder.HasIndex(item => new { item.PlanId, item.EffectiveFrom }).IsUnique();
        builder.HasOne<PlanEntity>()
            .WithMany()
            .HasForeignKey(item => item.PlanId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class MeterConfiguration : IEntityTypeConfiguration<MeterEntity>
{
    public void Configure(EntityTypeBuilder<MeterEntity> builder)
    {
        builder.ToTable("meters");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(160).IsRequired();
        builder.Property(item => item.Unit).HasMaxLength(64).IsRequired();
        builder.Property(item => item.Aggregation).HasConversion<string>();
        builder.Property(item => item.RoundingMode).HasConversion<string>();
        builder.Property(item => item.RoundingIncrement).HasPrecision(18, 6);
        builder.HasIndex(item => item.Name).IsUnique();
    }
}

public sealed class CustomerConfiguration : IEntityTypeConfiguration<CustomerEntity>
{
    public void Configure(EntityTypeBuilder<CustomerEntity> builder)
    {
        builder.ToTable("customers");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Name).HasMaxLength(200).IsRequired();
        builder.Property(item => item.Currency).HasMaxLength(3).IsRequired();
        builder.Property(item => item.CountryCode).HasMaxLength(2).IsRequired();
        builder.HasIndex(item => item.Name);
    }
}

public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<SubscriptionEntity>
{
    public void Configure(EntityTypeBuilder<SubscriptionEntity> builder)
    {
        builder.ToTable("subscriptions");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.State).HasConversion<string>();
        builder.Property(item => item.TrialEndBehavior).HasConversion<string>();
        builder.Property(item => item.StateBeforePause).HasConversion<string>();
        builder.Property(item => item.Version).IsConcurrencyToken();
        builder.HasIndex(item => new { item.CustomerId, item.State });
        builder.HasIndex(item => item.CurrentPeriodEnd);
        builder.HasOne<CustomerEntity>()
            .WithMany()
            .HasForeignKey(item => item.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PlanVersionEntity>()
            .WithMany()
            .HasForeignKey(item => item.PlanVersionId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CouponEntity>()
            .WithMany()
            .HasForeignKey(item => item.CouponId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public sealed class UsageEventConfiguration : IEntityTypeConfiguration<UsageEventEntity>
{
    public void Configure(EntityTypeBuilder<UsageEventEntity> builder)
    {
        builder.ToTable("usage_events");
        builder.HasKey(item => item.EventId);
        builder.Property(item => item.EventId).HasMaxLength(128);
        builder.Property(item => item.Quantity).HasPrecision(20, 6);
        builder.Property(item => item.Disposition).HasConversion<string>();
        builder.HasIndex(item => new { item.SubscriptionId, item.MeterId, item.OccurredAt });
        builder.HasIndex(item => item.AdjustmentOfEventId);
    }
}

public sealed class UsageRollupConfiguration : IEntityTypeConfiguration<UsageRollupEntity>
{
    public void Configure(EntityTypeBuilder<UsageRollupEntity> builder)
    {
        builder.ToTable("usage_rollups");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.AggregateValue).HasPrecision(20, 6);
        builder.Property(item => item.SumValue).HasPrecision(20, 6);
        builder.Property(item => item.MaxValue).HasPrecision(20, 6);
        builder.Property(item => item.LastValue).HasPrecision(20, 6);
        builder.HasIndex(item => new
        {
            item.SubscriptionId,
            item.MeterId,
            item.PeriodStart,
            item.PeriodEnd
        }).IsUnique();
    }
}

public sealed class InvoiceConfiguration : IEntityTypeConfiguration<InvoiceEntity>
{
    public void Configure(EntityTypeBuilder<InvoiceEntity> builder)
    {
        builder.ToTable("invoices");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Number).HasMaxLength(32);
        builder.Property(item => item.Status).HasConversion<string>();
        builder.Property(item => item.Currency).HasMaxLength(3).IsRequired();
        builder.Property(item => item.BillingReason).HasMaxLength(32).IsRequired();
        builder.HasIndex(item => item.Number).IsUnique();
        builder.HasIndex(item => new
        {
            item.SubscriptionId,
            item.PeriodStart,
            item.PeriodEnd,
            item.BillingReason
        }).IsUnique();
    }
}

public sealed class CouponConfiguration : IEntityTypeConfiguration<CouponEntity>
{
    public void Configure(EntityTypeBuilder<CouponEntity> builder)
    {
        builder.ToTable("coupons");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Code).HasMaxLength(64).IsRequired();
        builder.Property(item => item.Type).HasConversion<string>();
        builder.Property(item => item.Duration).HasConversion<string>();
        builder.Property(item => item.Percentage).HasPrecision(7, 4);
        builder.Property(item => item.Currency).HasMaxLength(3);
        builder.HasIndex(item => item.Code).IsUnique();
    }
}

public sealed class CreditConfiguration : IEntityTypeConfiguration<CreditEntity>
{
    public void Configure(EntityTypeBuilder<CreditEntity> builder)
    {
        builder.ToTable("credits");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Currency).HasMaxLength(3).IsRequired();
        builder.Property(item => item.Reason).HasMaxLength(500).IsRequired();
        builder.HasIndex(item => new { item.CustomerId, item.CreatedAt });
    }
}

public sealed class OperationalTablesConfiguration :
    IEntityTypeConfiguration<SubscriptionChangeEntity>,
    IEntityTypeConfiguration<PendingChargeEntity>,
    IEntityTypeConfiguration<UsageUniqueKeyEntity>,
    IEntityTypeConfiguration<InvoiceLineEntity>,
    IEntityTypeConfiguration<CouponRedemptionEntity>,
    IEntityTypeConfiguration<CreditApplicationEntity>,
    IEntityTypeConfiguration<CreditNoteEntity>,
    IEntityTypeConfiguration<PaymentAttemptEntity>,
    IEntityTypeConfiguration<DunningCaseEntity>,
    IEntityTypeConfiguration<WebhookNonceEntity>,
    IEntityTypeConfiguration<OutboundWebhookEntity>,
    IEntityTypeConfiguration<IdempotencyRecordEntity>,
    IEntityTypeConfiguration<AuditRecordEntity>,
    IEntityTypeConfiguration<SequenceEntity>
{
    public void Configure(EntityTypeBuilder<SubscriptionChangeEntity> builder)
    {
        builder.ToTable("subscription_changes");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Behavior).HasConversion<string>();
        builder.Property(item => item.Currency).HasMaxLength(3);
        builder.HasIndex(item => new { item.SubscriptionId, item.ChangedAt });
    }

    public void Configure(EntityTypeBuilder<PendingChargeEntity> builder)
    {
        builder.ToTable("pending_charges");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Type).HasConversion<string>();
        builder.Property(item => item.Currency).HasMaxLength(3);
        builder.HasIndex(item => new { item.SubscriptionId, item.Invoiced });
    }

    public void Configure(EntityTypeBuilder<UsageUniqueKeyEntity> builder)
    {
        builder.ToTable("usage_unique_keys");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.UniqueKey).HasMaxLength(256);
        builder.HasIndex(item => new
        {
            item.SubscriptionId,
            item.MeterId,
            item.PeriodStart,
            item.UniqueKey
        }).IsUnique();
    }

    public void Configure(EntityTypeBuilder<InvoiceLineEntity> builder)
    {
        builder.ToTable("invoice_lines");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Type).HasConversion<string>();
        builder.Property(item => item.Description).HasMaxLength(500);
        builder.Property(item => item.Currency).HasMaxLength(3);
        builder.HasIndex(item => item.InvoiceId);
        builder.HasOne<InvoiceEntity>()
            .WithMany()
            .HasForeignKey(item => item.InvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
    }

    public void Configure(EntityTypeBuilder<CouponRedemptionEntity> builder)
    {
        builder.ToTable("coupon_redemptions");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Currency).HasMaxLength(3);
        builder.HasIndex(item => new { item.CouponId, item.InvoiceId }).IsUnique();
    }

    public void Configure(EntityTypeBuilder<CreditApplicationEntity> builder)
    {
        builder.ToTable("credit_applications");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Currency).HasMaxLength(3);
        builder.HasIndex(item => new { item.CreditId, item.InvoiceId }).IsUnique();
    }

    public void Configure(EntityTypeBuilder<CreditNoteEntity> builder)
    {
        builder.ToTable("credit_notes");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Number).HasMaxLength(32);
        builder.Property(item => item.Currency).HasMaxLength(3);
        builder.HasIndex(item => item.Number).IsUnique();
    }

    public void Configure(EntityTypeBuilder<PaymentAttemptEntity> builder)
    {
        builder.ToTable("payment_attempts");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Outcome).HasConversion<string>();
        builder.HasIndex(item => new { item.InvoiceId, item.AttemptNumber }).IsUnique();
    }

    public void Configure(EntityTypeBuilder<DunningCaseEntity> builder)
    {
        builder.ToTable("dunning_cases");
        builder.HasKey(item => item.Id);
        builder.HasIndex(item => item.InvoiceId).IsUnique();
    }

    public void Configure(EntityTypeBuilder<WebhookNonceEntity> builder)
    {
        builder.ToTable("webhook_nonces");
        builder.HasKey(item => item.Nonce);
        builder.Property(item => item.Nonce).HasMaxLength(128);
        builder.HasIndex(item => item.ExpiresAt);
    }

    public void Configure(EntityTypeBuilder<OutboundWebhookEntity> builder)
    {
        builder.ToTable("outbound_webhooks");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Status).HasConversion<string>();
        builder.HasIndex(item => new { item.Status, item.NextAttemptAt });
    }

    public void Configure(EntityTypeBuilder<IdempotencyRecordEntity> builder)
    {
        builder.ToTable("idempotency_records");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Key).HasMaxLength(128);
        builder.Property(item => item.Route).HasMaxLength(256);
        builder.Property(item => item.RequestHash).HasMaxLength(64);
        builder.HasIndex(item => new { item.Key, item.Route }).IsUnique();
        builder.HasIndex(item => item.ExpiresAt);
    }

    public void Configure(EntityTypeBuilder<AuditRecordEntity> builder)
    {
        builder.ToTable("audit_records");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.StateHash).HasMaxLength(64);
        builder.HasIndex(item => new { item.ResourceType, item.ResourceId, item.OccurredAt });
    }

    public void Configure(EntityTypeBuilder<SequenceEntity> builder)
    {
        builder.ToTable("sequences");
        builder.HasKey(item => item.Name);
        builder.Property(item => item.Name).HasMaxLength(64);
    }
}
