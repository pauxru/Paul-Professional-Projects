using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence.Configurations;

public sealed class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> b)
    {
        b.ToTable("import_batches");
        b.HasKey(x => x.Id);
        b.Property(x => x.FileName).HasMaxLength(260).IsRequired();
        b.Property(x => x.ProfileName).HasMaxLength(100).IsRequired();
        b.Property(x => x.FileChecksum).HasMaxLength(64);
        b.HasIndex(x => x.FileChecksum);
        b.HasIndex(x => x.CreatedAtUtc);

        b.HasMany(x => x.Records)
            .WithOne()
            .HasForeignKey(r => r.ImportBatchId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.Rejections)
            .WithOne()
            .HasForeignKey(r => r.ImportBatchId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ImportRejectionConfiguration : IEntityTypeConfiguration<ImportRejection>
{
    public void Configure(EntityTypeBuilder<ImportRejection> b)
    {
        b.ToTable("import_rejections");
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.RawLine).HasMaxLength(1024);
        b.HasIndex(x => x.ImportBatchId);
    }
}
