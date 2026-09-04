using Collab.Domain.Audit;
using Collab.Domain.Comments;
using Collab.Domain.Documents;
using Collab.Domain.Identity;
using Collab.Domain.Notifications;
using Collab.Domain.Workspaces;

namespace Collab.Application.Abstractions;

public interface IUserRepository
{
    Task<User?> GetAsync(Guid id, CancellationToken ct = default);
    Task<User?> GetByEmailAsync(string email, CancellationToken ct = default);
    Task<bool> ExistsAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<User>> ListAsync(CancellationToken ct = default);
    void Add(User user);
}

public interface IWorkspaceRepository
{
    Task<Workspace?> GetAsync(Guid id, CancellationToken ct = default);
    Task<WorkspaceMember?> GetMemberAsync(Guid workspaceId, Guid userId, CancellationToken ct = default);
    Task<IReadOnlyList<WorkspaceMember>> ListMembersAsync(Guid workspaceId, CancellationToken ct = default);
    Task<IReadOnlyList<Workspace>> ListForUserAsync(Guid userId, CancellationToken ct = default);
    void Add(Workspace workspace);
    void AddMember(WorkspaceMember member);
}

public interface IDocumentRepository
{
    Task<Document?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Document>> ListByWorkspaceAsync(Guid workspaceId, int skip, int take, CancellationToken ct = default);
    Task<int> CountByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
    void Add(Document document);
}

public interface IOperationLogRepository
{
    void Add(OperationLogEntry entry);
    Task<IReadOnlyList<OperationLogEntry>> GetSinceAsync(Guid documentId, long afterSequence, CancellationToken ct = default);
    Task<IReadOnlyList<OperationLogEntry>> GetUpToAsync(Guid documentId, long throughSequence, CancellationToken ct = default);
    Task<IReadOnlyList<OperationLogEntry>> ListAsync(Guid documentId, int skip, int take, CancellationToken ct = default);
    Task<int> CountAsync(Guid documentId, CancellationToken ct = default);
    Task<long> MaxSequenceAsync(Guid documentId, CancellationToken ct = default);
}

public interface ISnapshotRepository
{
    void Add(DocumentSnapshot snapshot);
    Task<DocumentSnapshot?> GetLatestAsync(Guid documentId, CancellationToken ct = default);
    Task<DocumentSnapshot?> GetLatestAtOrBeforeAsync(Guid documentId, long sequence, CancellationToken ct = default);
}

public interface INamedVersionRepository
{
    void Add(NamedVersion version);
    Task<NamedVersion?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<NamedVersion>> ListAsync(Guid documentId, CancellationToken ct = default);
}

public interface ICommentRepository
{
    void Add(Comment comment);
    Task<Comment?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Comment>> ListByDocumentAsync(Guid documentId, CancellationToken ct = default);
    /// <summary>Text-anchored, non-orphaned comments for a document — the set that anchor rebasing touches.</summary>
    Task<IReadOnlyList<Comment>> ListAnchoredAsync(Guid documentId, CancellationToken ct = default);
}

public interface INotificationRepository
{
    void Add(Notification notification);
    Task<Notification?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<Notification>> ListForUserAsync(Guid userId, bool unreadOnly, int skip, int take, CancellationToken ct = default);
    Task<int> CountForUserAsync(Guid userId, bool unreadOnly, CancellationToken ct = default);
}

public interface IAuditRepository
{
    void Add(AuditRecord record);
    Task<IReadOnlyList<AuditRecord>> ListByDocumentAsync(Guid documentId, CancellationToken ct = default);
    Task<IReadOnlyList<AuditRecord>> ListByWorkspaceAsync(Guid workspaceId, CancellationToken ct = default);
}
