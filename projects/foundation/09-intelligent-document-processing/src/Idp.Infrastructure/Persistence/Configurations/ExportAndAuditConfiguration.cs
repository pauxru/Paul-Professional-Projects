using Idp.Domain.Audit;
using Idp.Domain.Exports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Idp.Infrastructure.Persistence.Configurations;

public sealed class ExportRecordConfiguration : IEntityTypeConfiguration<ExportRecord>
{
    public void Configure(EntityTypeBuilder<ExportRecord> builder)
    {
        builder.ToTable("ExportRecords");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.IdempotencyKey).HasMaxLength(128).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(e => e.Format).HasMaxLength(16);
        builder.Property(e => e.LastError).HasMaxLength(2048);
        builder.Property(e => e.OutboxPath).HasMaxLength(512);
        builder.Property(e => e.ErpReference).HasMaxLength(128);
        builder.HasIndex(e => e.IdempotencyKey).IsUnique();
        builder.HasIndex(e => e.DocumentId);
        builder.HasIndex(e => e.Status);
    }
}

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        builder.ToTable("AuditEntries");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Actor).HasMaxLength(128).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(64).IsRequired();
        builder.Property(a => a.Resource).HasMaxLength(256).IsRequired();
        builder.Property(a => a.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(a => a.BeforeHash).HasMaxLength(128);
        builder.Property(a => a.AfterHash).HasMaxLength(128);
        builder.Property(a => a.Detail).HasMaxLength(1024);
        builder.HasIndex(a => a.TimestampUtc);
        builder.HasIndex(a => a.Resource);
    }
}
