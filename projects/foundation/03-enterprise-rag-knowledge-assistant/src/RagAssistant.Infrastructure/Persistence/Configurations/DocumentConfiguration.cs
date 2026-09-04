using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using RagAssistant.Domain.Documents;

namespace RagAssistant.Infrastructure.Persistence.Configurations;

public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> builder)
    {
        builder.ToTable("documents");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Title).IsRequired().HasMaxLength(300);
        builder.Property(d => d.Source).IsRequired().HasMaxLength(500);
        builder.Property(d => d.Content).IsRequired();
        builder.Property(d => d.ContentHash).IsRequired().HasMaxLength(128);
        builder.HasIndex(d => d.ContentHash).IsUnique();
        builder.Property(d => d.CreatedAt).IsRequired();
        builder.Property(d => d.UpdatedAt).IsRequired();
        builder.Property(d => d.Version).IsConcurrencyToken();

        builder.OwnsOne(d => d.Acl, acl =>
        {
            acl.Property(a => a.Classification).HasConversion<int>().HasColumnName("acl_classification").IsRequired();
            acl.Property(a => a.Roles)
                .HasColumnName("acl_roles")
                .HasConversion(
                    v => string.Join('|', v),
                    v => string.IsNullOrEmpty(v)
                        ? Array.Empty<string>()
                        : v.Split('|', StringSplitOptions.RemoveEmptyEntries));
            acl.Property(a => a.Departments)
                .HasColumnName("acl_departments")
                .HasConversion(
                    v => string.Join('|', v),
                    v => string.IsNullOrEmpty(v)
                        ? Array.Empty<string>()
                        : v.Split('|', StringSplitOptions.RemoveEmptyEntries));
        });

        builder.HasMany(d => d.Chunks)
            .WithOne()
            .HasForeignKey(c => c.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        var chunksNav = builder.Metadata.FindNavigation(nameof(Document.Chunks))
            ?? throw new InvalidOperationException("Chunks navigation missing");
        chunksNav.SetPropertyAccessMode(PropertyAccessMode.Field);
    }
}
