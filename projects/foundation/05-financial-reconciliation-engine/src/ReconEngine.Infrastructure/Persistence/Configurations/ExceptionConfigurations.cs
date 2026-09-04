using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReconEngine.Domain.Entities;

namespace ReconEngine.Infrastructure.Persistence.Configurations;

public sealed class ReconciliationExceptionConfiguration : IEntityTypeConfiguration<ReconciliationException>
{
    public void Configure(EntityTypeBuilder<ReconciliationException> b)
    {
        b.ToTable("exceptions");
        b.HasKey(x => x.Id);

        b.Property(x => x.ExceptionKey).HasMaxLength(64).IsRequired();
        b.Property(x => x.SuggestedAction).HasMaxLength(500);
        b.Property(x => x.Currency).HasMaxLength(3);
        b.Property(x => x.RecordIdsJson).IsRequired();
        b.Property(x => x.AssignedTo).HasMaxLength(100);
        b.Property(x => x.ResolvedBy).HasMaxLength(100);
        b.Property(x => x.ApprovedBy).HasMaxLength(100);

        // Optimistic concurrency on the workflow aggregate.
        b.Property(x => x.Version).IsConcurrencyToken();

        b.HasIndex(x => x.ExceptionKey);
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.Type);
        b.HasIndex(x => x.Severity);
        b.HasIndex(x => x.Currency);
        b.HasIndex(x => x.AssignedTo);
        b.HasIndex(x => x.CreatedAtUtc);

        b.HasMany(x => x.Comments)
            .WithOne()
            .HasForeignKey(c => c.ExceptionId)
            .OnDelete(DeleteBehavior.Cascade);

        b.HasMany(x => x.AuditTrail)
            .WithOne()
            .HasForeignKey(a => a.ExceptionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class ExceptionCommentConfiguration : IEntityTypeConfiguration<ExceptionComment>
{
    public void Configure(EntityTypeBuilder<ExceptionComment> b)
    {
        b.ToTable("exception_comments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Author).HasMaxLength(100);
        b.Property(x => x.Text).HasMaxLength(2000);
        b.HasIndex(x => x.ExceptionId);
    }
}

public sealed class ExceptionAuditEntryConfiguration : IEntityTypeConfiguration<ExceptionAuditEntry>
{
    public void Configure(EntityTypeBuilder<ExceptionAuditEntry> b)
    {
        b.ToTable("exception_audit");
        b.HasKey(x => x.Id);
        b.Property(x => x.Actor).HasMaxLength(100);
        b.Property(x => x.Action).HasMaxLength(100);
        b.Property(x => x.Detail).HasMaxLength(2000);
        b.HasIndex(x => x.ExceptionId);
    }
}
