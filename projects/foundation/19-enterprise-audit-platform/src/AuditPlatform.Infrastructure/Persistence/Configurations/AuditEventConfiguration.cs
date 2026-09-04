using AuditPlatform.Domain.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AuditPlatform.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("AuditEvents");
        b.HasKey(x => x.Id);
        b.Property(x => x.TenantId).HasMaxLength(128).IsRequired();
        b.Property(x => x.EventType).HasMaxLength(200).IsRequired();
        b.Property(x => x.ActorId).HasMaxLength(200).IsRequired();
        b.Property(x => x.ActorDisplayName).HasMaxLength(200).IsRequired();
        b.Property(x => x.ActorRolesCsv).HasMaxLength(1024);
        b.Property(x => x.ActionVerb).HasMaxLength(64).IsRequired();
        b.Property(x => x.ResourceType).HasMaxLength(64).IsRequired();
        b.Property(x => x.ResourceId).HasMaxLength(200).IsRequired();
        b.Property(x => x.ResourceName).HasMaxLength(400);
        b.Property(x => x.ResourceParentPath).HasMaxLength(400);
        b.Property(x => x.SourceIp).HasMaxLength(64);
        b.Property(x => x.SourceUserAgent).HasMaxLength(400);
        b.Property(x => x.SourceService).HasMaxLength(200);
        b.Property(x => x.SourceRegion).HasMaxLength(64);
        b.Property(x => x.CorrelationId).HasMaxLength(200);
        b.Property(x => x.CausationId).HasMaxLength(200);
        b.Property(x => x.TraceId).HasMaxLength(200);
        b.Property(x => x.ClientEventId).HasMaxLength(200);
        b.Property(x => x.PayloadJson).IsRequired();
        b.Property(x => x.ContentHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.ChainHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.PreviousChainHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.BeforeHash).HasMaxLength(64);
        b.Property(x => x.AfterHash).HasMaxLength(64);
        b.Property(x => x.EventTime);
        b.Property(x => x.IngestTime);

        // Explicit indexes for every documented lookup path — see docs/database-schema.md.
        b.HasIndex(x => new { x.TenantId, x.SequenceNumber }).IsUnique();
        b.HasIndex(x => new { x.TenantId, x.EventTime });
        b.HasIndex(x => new { x.TenantId, x.ActorId, x.EventTime });
        b.HasIndex(x => new { x.TenantId, x.ResourceType, x.ResourceId });
        b.HasIndex(x => new { x.TenantId, x.CorrelationId });
        b.HasIndex(x => new { x.TenantId, x.Category, x.EventTime });
        b.HasIndex(x => new { x.TenantId, x.ClientEventId }).IsUnique().HasFilter("ClientEventId IS NOT NULL");
        b.HasIndex(x => x.ChainHash).IsUnique();
    }
}
