using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Northstar.Infrastructure.Persistence.Configurations;

public sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> builder)
    {
        builder.ToTable("AuditRecords");
        builder.HasKey(record => record.Id);
        builder.Property(record => record.Id).ValueGeneratedNever();
        builder.Property(record => record.Actor).HasMaxLength(200).IsRequired();
        builder.Property(record => record.Action).HasMaxLength(100).IsRequired();
        builder.Property(record => record.Resource).HasMaxLength(300).IsRequired();
        builder.Property(record => record.OccurredAt).HasConversion<string>();
        builder.Property(record => record.CorrelationId).HasMaxLength(128).IsRequired();
        builder.Property(record => record.SourceIp).HasMaxLength(64);
        builder.Property(record => record.UserAgent).HasMaxLength(512);
        builder.Property(record => record.BeforeHash).HasMaxLength(64).IsRequired();
        builder.Property(record => record.AfterHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(record => new { record.Resource, record.OccurredAt });
        builder.HasIndex(record => record.CorrelationId);
    }
}
