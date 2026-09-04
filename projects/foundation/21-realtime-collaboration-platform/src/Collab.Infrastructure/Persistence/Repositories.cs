using Collab.Application.Abstractions;
using Collab.Domain.Audit;
using Collab.Domain.Comments;
using Collab.Domain.Documents;
using Collab.Domain.Identity;
using Collab.Domain.Notifications;
using Collab.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace Collab.Infrastructure.Persistence;

/// <summary>
/// EF Core repository implementations. Mutations use the synchronous <c>Add</c> (change tracking
/// only); persistence happens when the shared <see cref="AppDbContext"/> is saved via
/// <see cref="IUnitOfWork"/>, so a whole use case commits atomically.
/// </summary>
public sealed class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct = default) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);

    public Task<bool> ExistsAsync(Guid id, CancellationToken ct = default) =>
        db.Users.AnyAsync(u => u.Id == id, ct);

    public async Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default) =>
        await db.Users.OrderBy(u => u.DisplayName).ToListAsync(ct);

    public void Add(User user) => db.Users.Add(user);
}

public sealed class WorkspaceRepository(AppDbContext db) : IWorkspaceRepository
{
    public Task<Workspace?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);

    public Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct = default) =>
        db.WorkspaceMembers.FirstOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, ct);

    public async Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId).OrderBy(m => m.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var workspaceIds = db.WorkspaceMembers.Where(m => m.UserId == userId).Select(m => m.WorkspaceId);
        return await db.Workspaces.Where(w => workspaceIds.Contains(w.Id)).OrderBy(w => w.Name).ToListAsync(ct);
    }

    public void Add(Workspace workspace) => db.Workspaces.Add(workspace);
    public void AddMember(WorkspaceMember member) => db.WorkspaceMembers.Add(member);
}

public sealed class DocumentRepository(AppDbContext db) : IDocumentRepository
{
    public Task<Document?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Documents.FirstOrDefaultAsync(d => d.Id == id, ct);

    public async Task<IReadOnlyList<Document>> ListByWorkspaceAsync(Guid workspaceId, int skip, int take, CancellationToken ct = default) =>
        await db.Documents.Where(d => d.WorkspaceId == workspaceId)
            .OrderByDescending(d => d.UpdatedAt).Skip(skip).Take(take).ToListAsync(ct);

    public Task<int> CountByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default) =>
        db.Documents.CountAsync(d => d.WorkspaceId == workspaceId, ct);

    public void Add(Document document) => db.Documents.Add(document);
}

public sealed class OperationLogRepository(AppDbContext db) : IOperationLogRepository
{
    public void Add(OperationLogEntry entry) => db.OperationLog.Add(entry);

    public async Task<IReadOnlyList<OperationLogEntry>> GetSinceAsync(Guid documentId, long afterSequence, CancellationToken ct = default) =>
        await db.OperationLog.Where(e => e.DocumentId == documentId && e.ServerSequence > afterSequence)
            .OrderBy(e => e.ServerSequence).ToListAsync(ct);

    public async Task<IReadOnlyList<OperationLogEntry>> GetUpToAsync(Guid documentId, long throughSequence, CancellationToken ct = default) =>
        await db.OperationLog.Where(e => e.DocumentId == documentId && e.ServerSequence <= throughSequence)
            .OrderBy(e => e.ServerSequence).ToListAsync(ct);

    public async Task<IReadOnlyList<OperationLogEntry>> ListAsync(Guid documentId, int skip, int take, CancellationToken ct = default) =>
        await db.OperationLog.Where(e => e.DocumentId == documentId)
            .OrderByDescending(e => e.ServerSequence).Skip(skip).Take(take).ToListAsync(ct);

    public Task<int> CountAsync(Guid documentId, CancellationToken ct = default) =>
        db.OperationLog.CountAsync(e => e.DocumentId == documentId, ct);

    public async Task<long> MaxSequenceAsync(Guid documentId, CancellationToken ct = default) =>
        await db.OperationLog.Where(e => e.DocumentId == documentId)
            .MaxAsync(e => (long?)e.ServerSequence, ct) ?? 0;
}

public sealed class SnapshotRepository(AppDbContext db) : ISnapshotRepository
{
    public void Add(DocumentSnapshot snapshot) => db.Snapshots.Add(snapshot);

    public Task<DocumentSnapshot?> GetLatestAsync(Guid documentId, CancellationToken ct = default) =>
        db.Snapshots.Where(s => s.DocumentId == documentId)
            .OrderByDescending(s => s.AtSequence).FirstOrDefaultAsync(ct);

    public Task<DocumentSnapshot?> GetLatestAtOrBeforeAsync(Guid documentId, long sequence, CancellationToken ct = default) =>
        db.Snapshots.Where(s => s.DocumentId == documentId && s.AtSequence <= sequence)
            .OrderByDescending(s => s.AtSequence).FirstOrDefaultAsync(ct);
}

public sealed class NamedVersionRepository(AppDbContext db) : INamedVersionRepository
{
    public void Add(NamedVersion version) => db.NamedVersions.Add(version);

    public Task<NamedVersion?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.NamedVersions.FirstOrDefaultAsync(v => v.Id == id, ct);

    public async Task<IReadOnlyList<NamedVersion>> ListAsync(Guid documentId, CancellationToken ct = default) =>
        await db.NamedVersions.Where(v => v.DocumentId == documentId)
            .OrderByDescending(v => v.AtSequence).ToListAsync(ct);
}

public sealed class CommentRepository(AppDbContext db) : ICommentRepository
{
    public void Add(Comment comment) => db.Comments.Add(comment);

    public Task<Comment?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Comments.FirstOrDefaultAsync(c => c.Id == id, ct);

    public async Task<IReadOnlyList<Comment>> ListByDocumentAsync(Guid documentId, CancellationToken ct = default) =>
        await db.Comments.Where(c => c.DocumentId == documentId)
            .OrderBy(c => c.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<Comment>> ListAnchoredAsync(Guid documentId, CancellationToken ct = default) =>
        await db.Comments
            .Where(c => c.DocumentId == documentId && c.AnchorKind == AnchorKind.TextRange && !c.IsOrphaned)
            .ToListAsync(ct);
}

public sealed class NotificationRepository(AppDbContext db) : INotificationRepository
{
    public void Add(Notification notification) => db.Notifications.Add(notification);

    public Task<Notification?> GetAsync(Guid id, CancellationToken ct = default) =>
        db.Notifications.FirstOrDefaultAsync(n => n.Id == id, ct);

    public async Task<IReadOnlyList<Notification>> ListForUserAsync(Guid userId, bool unreadOnly, int skip, int take, CancellationToken ct = default)
    {
        var query = db.Notifications.Where(n => n.UserId == userId);
        if (unreadOnly) query = query.Where(n => !n.IsRead);
        return await query.OrderByDescending(n => n.CreatedAt).Skip(skip).Take(take).ToListAsync(ct);
    }

    public Task<int> CountForUserAsync(Guid userId, bool unreadOnly, CancellationToken ct = default) =>
        unreadOnly
            ? db.Notifications.CountAsync(n => n.UserId == userId && !n.IsRead, ct)
            : db.Notifications.CountAsync(n => n.UserId == userId, ct);
}

public sealed class AuditRepository(AppDbContext db) : IAuditRepository
{
    public void Add(AuditRecord record) => db.AuditRecords.Add(record);

    public async Task<IReadOnlyList<AuditRecord>> ListByDocumentAsync(Guid documentId, CancellationToken ct = default) =>
        await db.AuditRecords.Where(a => a.DocumentId == documentId)
            .OrderByDescending(a => a.CreatedAt).ToListAsync(ct);

    public async Task<IReadOnlyList<AuditRecord>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default) =>
        await db.AuditRecords.Where(a => a.WorkspaceId == workspaceId)
            .OrderByDescending(a => a.CreatedAt).ToListAsync(ct);
}
