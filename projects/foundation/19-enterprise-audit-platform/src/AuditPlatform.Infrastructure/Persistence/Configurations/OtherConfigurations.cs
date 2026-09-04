using AuditPlatform.Domain.Events;
using AuditPlatform.Domain.Integrity;
using AuditPlatform.Domain.Retention;
using AuditPlatform.Domain.Schemas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuditPlatform.Infrastructure.Persistence.Configurations;

public sealed class EventSchemaConfiguration : IEntityTypeConfiguration<EventSchema>
{
    public void Configure(EntityTypeBuilder<EventSchema> b)
    {
        b.ToTable("EventSchemas");
        b.HasKey(x => x.Id);
        b.Property(x => x.EventType).HasMaxLength(200).IsRequired();
        b.Property(x => x.SchemaJson).IsRequired();
        b.Property(x => x.Description).HasMaxLength(500);
        b.HasIndex(x => new { x.EventType, x.Version }).IsUnique();
    }
}

public sealed class CheckpointConfiguration : IEntityTypeConfiguration<Checkpoint>
{
    public void Configure(EntityTypeBuilder<Checkpoint> b)
    {
        b.ToTable("Checkpoints");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.MerkleRoot).HasMaxLength(64).IsRequired();
        b.Property(x => x.FirstChainHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.LastChainHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.SignatureBase64).HasMaxLength(1024);
        b.Property(x => x.SigningKeyId).HasMaxLength(128);
        b.HasIndex(x => new { x.TenantId, x.FromSequence, x.ToSequence });
    }
}

public sealed class RetentionPolicyConfiguration : IEntityTypeConfiguration<RetentionPolicy>
{
    public void Configure(EntityTypeBuilder<RetentionPolicy> b)
    {
        b.ToTable("RetentionPolicies");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.CategoryPattern).HasMaxLength(64).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.CategoryPattern }).IsUnique();
    }
}

public sealed class LegalHoldConfiguration : IEntityTypeConfiguration<LegalHold>
{
    public void Configure(EntityTypeBuilder<LegalHold> b)
    {
        b.ToTable("LegalHolds");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.ResourceType).HasMaxLength(64).IsRequired();
        b.Property(x => x.ResourceId).HasMaxLength(200).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.Property(x => x.TicketReference).HasMaxLength(200).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.ResourceType, x.ResourceId });
    }
}

public sealed class SavedQueryConfiguration : IEntityTypeConfiguration<SavedQuery>
{
    public void Configure(EntityTypeBuilder<SavedQuery> b)
    {
        b.ToTable("SavedQueries");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.Property(x => x.QueryJson).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
    }
}

public sealed class DeadLetterEventConfiguration : IEntityTypeConfiguration<DeadLetterEvent>
{
    public void Configure(EntityTypeBuilder<DeadLetterEvent> b)
    {
        b.ToTable("DeadLetter");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.RawPayload).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(1000).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.ReceivedAt });
    }
}

public sealed class SearchIndexEntryConfiguration : IEntityTypeConfiguration<SearchIndexEntry>
{
    public void Configure(EntityTypeBuilder<SearchIndexEntry> b)
    {
        b.ToTable("SearchIndex");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedOnAdd();
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.Text).IsRequired();
        b.HasIndex(x => new { x.TenantId, x.EventId });
    }
}
