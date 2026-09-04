namespace NotificationPlatform.Infrastructure.Persistence.Configurations;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NotificationPlatform.Domain.Notifications;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.TemplateKey).HasMaxLength(150).IsRequired();
        b.Property(x => x.IdempotencyKey).HasMaxLength(120);
        b.Property(x => x.DeduplicationKey).HasMaxLength(120);
        b.Property(x => x.PayloadJson).IsRequired();
        b.Property(x => x.Locale).HasMaxLength(20);
        b.Property(x => x.Address).HasMaxLength(500);
        b.Property(x => x.RenderedSubject).HasMaxLength(500);
        b.Property(x => x.LastProvider).HasMaxLength(100);
        b.Property(x => x.LastError).HasMaxLength(1000);
        b.Property(x => x.CorrelationId).HasMaxLength(80);
        b.HasIndex(x => new { x.TenantId, x.Status });
        b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        b.HasIndex(x => new { x.TenantId, x.IdempotencyKey });
        b.HasIndex(x => new { x.TenantId, x.RecipientId, x.TemplateKey, x.DeduplicationKey });
        b.HasIndex(x => x.ScheduledFor);
        b.HasMany(x => x.Attempts).WithOne().HasForeignKey(a => a.NotificationId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(x => x.Attempts).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class NotificationAttemptConfiguration : IEntityTypeConfiguration<NotificationAttempt>
{
    public void Configure(EntityTypeBuilder<NotificationAttempt> b)
    {
        b.ToTable("notification_attempts");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProviderName).HasMaxLength(100).IsRequired();
        b.Property(x => x.Error).HasMaxLength(1000);
        b.Property(x => x.ProviderMessageId).HasMaxLength(100);
        b.HasIndex(x => x.NotificationId);
    }
}

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("idempotency");
        b.HasKey(x => x.Id);
        b.Property(x => x.Key).HasMaxLength(120).IsRequired();
        b.Property(x => x.RequestHash).HasMaxLength(120).IsRequired();
        b.Property(x => x.ResponseJson).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
    }
}

public sealed class DeliveryReceiptConfiguration : IEntityTypeConfiguration<DeliveryReceipt>
{
    public void Configure(EntityTypeBuilder<DeliveryReceipt> b)
    {
        b.ToTable("receipts");
        b.HasKey(x => x.Id);
        b.Property(x => x.ProviderName).HasMaxLength(100).IsRequired();
        b.Property(x => x.ProviderMessageId).HasMaxLength(100).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(40).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.Nonce).HasMaxLength(100).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.NotificationId });
        b.HasIndex(x => x.Nonce).IsUnique();
    }
}
