using Collab.Application.Abstractions;
using Collab.Domain.Audit;
using Collab.Domain.Comments;
using Collab.Domain.Documents;
using Collab.Domain.Identity;
using Collab.Domain.Notifications;
using Collab.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Collab.Infrastructure.Persistence;

/// <summary>
/// The EF Core unit of work. Also implements <see cref="IUnitOfWork"/> so the Application layer can
/// commit a set of repository mutations atomically without depending on EF.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options), IUnitOfWork
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<WorkspaceMember> WorkspaceMembers => Set<WorkspaceMember>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<OperationLogEntry> OperationLog => Set<OperationLogEntry>();
    public DbSet<DocumentSnapshot> Snapshots => Set<DocumentSnapshot>();
    public DbSet<NamedVersion> NamedVersions => Set<NamedVersion>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot ORDER BY / compare DateTimeOffset natively. Persist every timestamp as UTC
        // ticks (a long), which is both SQL-sortable and round-trips losslessly for our UTC clock.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }

    /// <summary>Stores a <see cref="DateTimeOffset"/> as UTC ticks so SQLite can order and compare it.</summary>
    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));
}
