using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence.Configurations;

public sealed class ReconRecordConfiguration : IEntityTypeConfiguration<ReconRecord>
{
    public void Configure(EntityTypeBuilder<ReconRecord> b)
    {
        b.ToTable("recon_records");
        b.HasKey(x => x.Id);

        b.Property(x => x.RawReference).HasMaxLength(200);
        b.Property(x => x.CanonicalReference).HasMaxLength(200);
        b.Property(x => x.CounterpartyReference).HasMaxLength(200);
        b.Property(x => x.Currency).HasMaxLength(3).IsRequired();
        b.Property(x => x.RowHash).HasMaxLength(64).IsRequired();
        b.Property(x => x.SourceTimeZone).HasMaxLength(6);

        b.Ignore(x => x.Amount);
        b.Ignore(x => x.Fee);

        // Hot-path lookups used by the matching engine and reports.
        b.HasIndex(x => new { x.Currency, x.ValueDate });
        b.HasIndex(x => x.CanonicalReference);
        b.HasIndex(x => x.RowHash);
        b.HasIndex(x => x.ReconStatus);
        b.HasIndex(x => x.Source);
        b.HasIndex(x => x.LastRunId);
        b.HasIndex(x => new { x.ReconStatus, x.ValueDate });
    }
}
