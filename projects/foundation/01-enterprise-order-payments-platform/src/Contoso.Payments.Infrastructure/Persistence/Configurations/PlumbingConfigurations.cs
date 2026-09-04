using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Contoso.Payments.Application.Idempotency;
using Contoso.Payments.Application.Outbox;
using Contoso.Payments.Application.Reconciliation;
using Contoso.Payments.Application.Webhooks;

namespace Contoso.Payments.Infrastructure.Persistence.Configurations;

public sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> b)
    {
        b.ToTable("IdempotencyRecords");
        b.HasKey(r => new { r.Key, r.Endpoint });
        b.Property(r => r.Key).HasMaxLength(128).IsRequired();
        b.Property(r => r.Endpoint).HasMaxLength(256).IsRequired();
        b.Property(r => r.RequestHash).HasMaxLength(64).IsRequired();
        b.Property(r => r.ResponseStatus).IsRequired();
        b.Property(r => r.ResponseBody).IsRequired();
        b.Property(r => r.ResponseContentType).HasMaxLength(64).IsRequired();
        b.Property(r => r.CorrelationId).HasMaxLength(64);
        b.Property(r => r.CreatedAtUtc).IsRequired();
        b.HasIndex(r => r.Key);
    }
}

public sealed class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> b)
    {
        b.ToTable("OutboxMessages");
        b.HasKey(m => m.Id);
        b.Property(m => m.Topic).HasMaxLength(128).IsRequired();
        b.Property(m => m.PayloadJson).IsRequired();
        b.Property(m => m.OccurredAtUtc).IsRequired();
        b.Property(m => m.NextAttemptAtUtc).IsRequired();
        b.Property(m => m.Attempts).IsRequired();
        b.Property(m => m.LastError).HasMaxLength(1024);
        b.Property(m => m.CorrelationId).HasMaxLength(64);
        b.Property(m => m.Dispatched).IsRequired();
        b.HasIndex(m => new { m.Dispatched, m.NextAttemptAtUtc });
    }
}

public sealed class OutboxDeadLetterConfiguration : IEntityTypeConfiguration<OutboxDeadLetter>
{
    public void Configure(EntityTypeBuilder<OutboxDeadLetter> b)
    {
        b.ToTable("OutboxDeadLetters");
        b.HasKey(m => m.Id);
        b.Property(m => m.OriginalMessageId).IsRequired();
        b.Property(m => m.Topic).HasMaxLength(128).IsRequired();
        b.Property(m => m.PayloadJson).IsRequired();
        b.Property(m => m.LastError).HasMaxLength(1024);
        b.Property(m => m.CorrelationId).HasMaxLength(64);
        b.Property(m => m.DeadLetteredAtUtc).IsRequired();
    }
}

public sealed class WebhookReplayConfiguration : IEntityTypeConfiguration<WebhookReplayRecord>
{
    public void Configure(EntityTypeBuilder<WebhookReplayRecord> b)
    {
        b.ToTable("WebhookReplays");
        b.HasKey(r => r.SignatureHash);
        b.Property(r => r.SignatureHash).HasMaxLength(64).IsRequired();
        b.Property(r => r.ReceivedAtUtc).IsRequired();
    }
}

public sealed class ReconciliationRunConfiguration : IEntityTypeConfiguration<ReconciliationRun>
{
    public void Configure(EntityTypeBuilder<ReconciliationRun> b)
    {
        b.ToTable("ReconciliationRuns");
        b.HasKey(r => r.Id);
        b.Property(r => r.SourceFileName).HasMaxLength(256).IsRequired();
        b.HasIndex(r => r.StartedAtUtc);
    }
}

public sealed class ReconciliationDiscrepancyConfiguration : IEntityTypeConfiguration<ReconciliationDiscrepancy>
{
    public void Configure(EntityTypeBuilder<ReconciliationDiscrepancy> b)
    {
        b.ToTable("ReconciliationDiscrepancies");
        b.HasKey(r => r.Id);
        b.Property(r => r.Kind).HasConversion<int>().IsRequired();
        b.Property(r => r.PaymentIntentId).HasMaxLength(64);
        b.Property(r => r.ProviderReference).HasMaxLength(128);
        b.Property(r => r.Currency).HasMaxLength(3);
        b.Property(r => r.InternalStatus).HasMaxLength(32);
        b.Property(r => r.ProviderStatus).HasMaxLength(32);
        b.Property(r => r.Notes).HasMaxLength(1024);
        b.HasIndex(r => r.RunId);
        b.HasIndex(r => r.Kind);
    }
}
