using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Contoso.Payments.Domain.Audit;

namespace Contoso.Payments.Infrastructure.Persistence.Configurations;

public sealed class AuditEventConfiguration : IEntityTypeConfiguration<AuditEvent>
{
    public void Configure(EntityTypeBuilder<AuditEvent> b)
    {
        b.ToTable("AuditEvents");
        b.HasKey(a => a.Id);
        b.Property(a => a.Actor).HasMaxLength(128).IsRequired();
        b.Property(a => a.Action).HasMaxLength(128).IsRequired();
        b.Property(a => a.Resource).HasMaxLength(256).IsRequired();
        b.Property(a => a.CorrelationId).HasMaxLength(64).IsRequired();
        b.Property(a => a.BeforeHash).HasMaxLength(64);
        b.Property(a => a.AfterHash).HasMaxLength(64);
        b.Property(a => a.AtUtc).IsRequired();
        b.HasIndex(a => a.AtUtc);
        b.HasIndex(a => a.Resource);
        b.HasIndex(a => a.CorrelationId);
    }
}
