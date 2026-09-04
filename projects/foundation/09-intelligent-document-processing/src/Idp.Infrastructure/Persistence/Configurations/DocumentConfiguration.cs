using Idp.Domain.Documents;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Idp.Infrastructure.Persistence.Configurations;

public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("Documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();

        builder.Property(d => d.FileName).HasMaxLength(512).IsRequired();
        builder.Property(d => d.ContentType).HasMaxLength(128).IsRequired();
        builder.Property(d => d.ContentHash).HasMaxLength(64).IsRequired();
        builder.Property(d => d.StorageKey).HasMaxLength(512).IsRequired();
        builder.Property(d => d.CorrelationId).HasMaxLength(64).IsRequired();
        builder.Property(d => d.SupplierNameRaw).HasMaxLength(512);
        builder.Property(d => d.Currency).HasMaxLength(8);
        builder.Property(d => d.ClassificationExplanation).HasMaxLength(2048);
        builder.Property(d => d.DocumentValue).HasPrecision(18, 2);

        builder.Property(d => d.DocumentType).HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.State).HasConversion<string>().HasMaxLength(32);
        builder.Property(d => d.Routing).HasConversion<string>().HasMaxLength(32);

        builder.HasIndex(d => d.ContentHash);
        builder.HasIndex(d => d.State);
        builder.HasIndex(d => d.DocumentType);

        ConfigureChildren(builder);
    }

    private static void ConfigureChildren(EntityTypeBuilder<Document> builder)
    {
        builder.HasMany(d => d.Fields).WithOne().HasForeignKey(f => f.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(d => d.LineItems).WithOne().HasForeignKey("DocumentId")
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(d => d.Transitions).WithOne().HasForeignKey(t => t.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(d => d.Validations).WithOne().HasForeignKey("DocumentId")
            .OnDelete(DeleteBehavior.Cascade);

        SetFieldAccess(builder, nameof(Document.Fields), "_fields");
        SetFieldAccess(builder, nameof(Document.LineItems), "_lineItems");
        SetFieldAccess(builder, nameof(Document.Transitions), "_transitions");
        SetFieldAccess(builder, nameof(Document.Validations), "_validations");
    }

    private static void SetFieldAccess(
        EntityTypeBuilder<Document> builder, string navigation, string field)
    {
        var nav = builder.Metadata.FindNavigation(navigation)!;
        nav.SetField(field);
        nav.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}

public sealed class ExtractedFieldConfiguration : IEntityTypeConfiguration<ExtractedField>
{
    public void Configure(EntityTypeBuilder<ExtractedField> builder)
    {
        builder.ToTable("ExtractedFields");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.FieldKey).HasMaxLength(64).IsRequired();
        builder.Property(f => f.RawValue).HasMaxLength(1024);
        builder.Property(f => f.NormalizedValue).HasMaxLength(1024);
        builder.Property(f => f.SourceText).HasMaxLength(1024);
        builder.Property(f => f.Strategy).HasConversion<string>().HasMaxLength(32);
        builder.HasIndex(f => new { f.DocumentId, f.FieldKey });
    }
}

public sealed class LineItemConfiguration : IEntityTypeConfiguration<LineItem>
{
    public void Configure(EntityTypeBuilder<LineItem> builder)
    {
        builder.ToTable("LineItems");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Description).HasMaxLength(512);
        builder.Property(l => l.Quantity).HasPrecision(18, 4);
        builder.Property(l => l.UnitPrice).HasPrecision(18, 4);
        builder.Property(l => l.LineTotal).HasPrecision(18, 2);
        builder.Property(l => l.TaxRate).HasPrecision(9, 4);
        builder.Property<Guid>("DocumentId");
        builder.HasIndex("DocumentId");
    }
}

public sealed class PipelineTransitionConfiguration : IEntityTypeConfiguration<PipelineTransition>
{
    public void Configure(EntityTypeBuilder<PipelineTransition> builder)
    {
        builder.ToTable("PipelineTransitions");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();
        builder.Property(t => t.FromState).HasConversion<string>().HasMaxLength(32);
        builder.Property(t => t.ToState).HasConversion<string>().HasMaxLength(32);
        builder.Property(t => t.Reason).HasMaxLength(512);
        builder.Property(t => t.Actor).HasMaxLength(128);
        builder.HasIndex(t => t.DocumentId);
    }
}

public sealed class DocumentValidationConfiguration : IEntityTypeConfiguration<DocumentValidation>
{
    public void Configure(EntityTypeBuilder<DocumentValidation> builder)
    {
        builder.ToTable("DocumentValidations");
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.RuleName).HasMaxLength(64).IsRequired();
        builder.Property(v => v.Outcome).HasConversion<string>().HasMaxLength(16);
        builder.Property(v => v.Message).HasMaxLength(1024);
        builder.Property(v => v.ImplicatedFields).HasMaxLength(512);
        builder.Property<Guid>("DocumentId");
        builder.HasIndex("DocumentId");
    }
}
