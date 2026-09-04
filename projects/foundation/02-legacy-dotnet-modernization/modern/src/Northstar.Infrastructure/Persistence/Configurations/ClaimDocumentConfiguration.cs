using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Northstar.Domain.Claims;

namespace Northstar.Infrastructure.Persistence.Configurations;

public sealed class ClaimDocumentConfiguration : IEntityTypeConfiguration<ClaimDocument>
{
    public void Configure(EntityTypeBuilder<ClaimDocument> builder)
    {
        builder.ToTable("ClaimDocuments");
        builder.HasKey(document => document.Id);
        builder.Property(document => document.Id).ValueGeneratedNever();
        builder.Property(document => document.ClaimId).IsRequired();
        builder.HasIndex(document => document.ClaimId);
        builder.Property(document => document.OriginalName).HasMaxLength(255).IsRequired();
        builder.Property(document => document.ContentType).HasMaxLength(100).IsRequired();
        builder.Property(document => document.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(document => document.UploadedAt).HasConversion<string>();
    }
}
