using Collab.Domain.Audit;
using Collab.Domain.Comments;
using Collab.Domain.Documents;
using Collab.Domain.Identity;
using Collab.Domain.Notifications;
using Collab.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Collab.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        b.Property(x => x.Email).HasMaxLength(320).IsRequired();
        b.HasIndex(x => x.Email).IsUnique();
    }
}

public sealed class WorkspaceConfiguration : IEntityTypeConfiguration<Workspace>
{
    public void Configure(EntityTypeBuilder<Workspace> b)
    {
        b.ToTable("workspaces");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
    }
}

public sealed class WorkspaceMemberConfiguration : IEntityTypeConfiguration<WorkspaceMember>
{
    public void Configure(EntityTypeBuilder<WorkspaceMember> b)
    {
        b.ToTable("workspace_members");
        b.HasKey(x => x.Id);
        b.Property(x => x.Role).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
    }
}

public sealed class DocumentConfiguration : IEntityTypeConfiguration<Document>
{
    public void Configure(EntityTypeBuilder<Document> b)
    {
        b.ToTable("documents");
        b.HasKey(x => x.Id);
        b.Property(x => x.Title).HasMaxLength(300).IsRequired();
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.WorkspaceId);
    }
}

public sealed class OperationLogEntryConfiguration : IEntityTypeConfiguration<OperationLogEntry>
{
    public void Configure(EntityTypeBuilder<OperationLogEntry> b)
    {
        b.ToTable("operation_log");
        b.HasKey(x => x.Id);
        b.Property(x => x.AuthorReplicaId).HasMaxLength(64).IsRequired();
        b.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Payload).IsRequired();
        // Gap-free, monotonic per document: the authoritative version axis.
        b.HasIndex(x => new { x.DocumentId, x.ServerSequence }).IsUnique();
    }
}

public sealed class DocumentSnapshotConfiguration : IEntityTypeConfiguration<DocumentSnapshot>
{
    public void Configure(EntityTypeBuilder<DocumentSnapshot> b)
    {
        b.ToTable("snapshots");
        b.HasKey(x => x.Id);
        b.Property(x => x.State).IsRequired();
        b.Property(x => x.MaterializedContent).IsRequired();
        b.HasIndex(x => new { x.DocumentId, x.AtSequence }).IsUnique();
    }
}

public sealed class NamedVersionConfiguration : IEntityTypeConfiguration<NamedVersion>
{
    public void Configure(EntityTypeBuilder<NamedVersion> b)
    {
        b.ToTable("named_versions");
        b.HasKey(x => x.Id);
        b.Property(x => x.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(x => x.DocumentId);
    }
}

public sealed class CommentConfiguration : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> b)
    {
        b.ToTable("comments");
        b.HasKey(x => x.Id);
        b.Property(x => x.Body).IsRequired();
        b.Property(x => x.AnchorKind).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.Property(x => x.FieldPath).HasMaxLength(400);
        b.Property(x => x.MentionsCsv).HasMaxLength(4000);
        b.HasIndex(x => x.DocumentId);
        b.HasIndex(x => x.ThreadId);
        // Computed projections over stored columns — not mapped.
        b.Ignore(x => x.Mentions);
        b.Ignore(x => x.Anchor);
    }
}

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> b)
    {
        b.ToTable("notifications");
        b.HasKey(x => x.Id);
        b.Property(x => x.Type).HasConversion<string>().HasMaxLength(30);
        b.Property(x => x.Message).HasMaxLength(1000).IsRequired();
        b.HasIndex(x => new { x.UserId, x.IsRead });
    }
}

public sealed class AuditRecordConfiguration : IEntityTypeConfiguration<AuditRecord>
{
    public void Configure(EntityTypeBuilder<AuditRecord> b)
    {
        b.ToTable("audit_records");
        b.HasKey(x => x.Id);
        b.Property(x => x.Action).HasMaxLength(100).IsRequired();
        b.Property(x => x.ResourceType).HasMaxLength(50).IsRequired();
        b.Property(x => x.ResourceId).HasMaxLength(100).IsRequired();
        b.Property(x => x.Details).HasMaxLength(2000);
        b.Property(x => x.CorrelationId).HasMaxLength(100);
        b.HasIndex(x => x.DocumentId);
        b.HasIndex(x => x.WorkspaceId);
        b.HasIndex(x => x.CreatedAt);
    }
}
