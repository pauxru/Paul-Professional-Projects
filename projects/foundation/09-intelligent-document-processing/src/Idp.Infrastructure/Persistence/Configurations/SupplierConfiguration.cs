using Idp.Domain.Review;
using Idp.Domain.Suppliers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Idp.Infrastructure.Persistence.Configurations;

public sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("Suppliers");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.Property(s => s.Name).HasMaxLength(512).IsRequired();
        builder.Property(s => s.TaxId).HasMaxLength(64);
        builder.Property(s => s.Aliases).HasMaxLength(2048);
        builder.Property(s => s.DefaultCurrency).HasMaxLength(8);
        builder.Property(s => s.BankAccount).HasMaxLength(128);
        builder.HasIndex(s => s.Name);

        builder.HasMany(s => s.Hints).WithOne().HasForeignKey(h => h.SupplierId)
            .OnDelete(DeleteBehavior.Cascade);
        var nav = builder.Metadata.FindNavigation(nameof(Supplier.Hints))!;
        nav.SetField("_hints");
        nav.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class SupplierHintConfiguration : IEntityTypeConfiguration<SupplierHint>
{
    public void Configure(EntityTypeBuilder<SupplierHint> builder)
    {
        builder.ToTable("SupplierHints");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.Property(h => h.FieldKey).HasMaxLength(64).IsRequired();
        builder.Property(h => h.AnchorText).HasMaxLength(256).IsRequired();
        builder.HasIndex(h => new { h.SupplierId, h.FieldKey });
    }
}

public sealed class ReviewTaskConfiguration : IEntityTypeConfiguration<ReviewTask>
{
    public void Configure(EntityTypeBuilder<ReviewTask> builder)
    {
        builder.ToTable("ReviewTasks");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.Resolution).HasConversion<string>().HasMaxLength(16);
        builder.Property(r => r.ClaimedBy).HasMaxLength(128);
        builder.Property(r => r.CompletedBy).HasMaxLength(128);
        builder.Property(r => r.DocumentValue).HasPrecision(18, 2);
        builder.HasIndex(r => r.DocumentId).IsUnique();
        builder.HasIndex(r => new { r.Status, r.Priority });
    }
}

public sealed class CorrectionConfiguration : IEntityTypeConfiguration<Correction>
{
    public void Configure(EntityTypeBuilder<Correction> builder)
    {
        builder.ToTable("Corrections");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.FieldKey).HasMaxLength(64).IsRequired();
        builder.Property(c => c.OldValue).HasMaxLength(1024);
        builder.Property(c => c.NewValue).HasMaxLength(1024);
        builder.Property(c => c.Reason).HasMaxLength(512);
        builder.Property(c => c.Reviewer).HasMaxLength(128);
        builder.HasIndex(c => c.DocumentId);
    }
}
