using Collab.Domain.Documents;

namespace Collab.Application.Contracts;

/// <summary>Full document state handed to a client on join or full resync.</summary>
public sealed record DocumentStateDto(
    Guid DocumentId,
    DocumentType Type,
    long Sequence,
    string State,
    string Content);

/// <summary>
/// Result of a resync request. Either a full checkpoint (client had no/stale state) plus the tail,
/// or an incremental list of operations the client missed while offline.
/// </summary>
public sealed record ResyncResult(
    bool Full,
    DocumentStateDto? Checkpoint,
    IReadOnlyList<OperationBroadcast> Operations,
    long CurrentSequence);

public sealed record CreateWorkspaceRequest(string Name);
public sealed record AddMemberRequest(Guid UserId, string Role);
public sealed record ChangeRoleRequest(string Role);
public sealed record WorkspaceDto(Guid Id, string Name, DateTimeOffset CreatedAt);
public sealed record MemberDto(Guid Id, Guid UserId, string DisplayName, string Role);

public sealed record CreateDocumentRequest(Guid WorkspaceId, string Title, string Type);
public sealed record DocumentDto(
    Guid Id, Guid WorkspaceId, string Title, string Type, long CurrentSequence,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record DocumentContentDto(Guid Id, string Title, string Type, long Sequence, string Content);

public sealed record OperationLogDto(long Sequence, Guid AuthorUserId, string Kind, DateTimeOffset CreatedAt);
public sealed record CreateNamedVersionRequest(string Name);
public sealed record NamedVersionDto(Guid Id, string Name, long AtSequence, DateTimeOffset CreatedAt);
public sealed record DiffRequest(long FromSequence, long ToSequence);
public sealed record DiffSegmentDto(string Kind, string Text);
public sealed record DiffResultDto(long FromSequence, long ToSequence, IReadOnlyList<DiffSegmentDto> Segments);
public sealed record RestoreRequest(long ToSequence);

public sealed record CreateCommentRequest(
    string Body, int? AnchorStart, int? AnchorEnd, string? FieldPath, IReadOnlyList<Guid>? Mentions);
public sealed record ReplyRequest(string Body, IReadOnlyList<Guid>? Mentions);
public sealed record CommentDto(
    Guid Id, Guid DocumentId, Guid ThreadId, Guid? ParentCommentId, Guid AuthorUserId, string Body,
    string AnchorKind, int AnchorStart, int AnchorEnd, string? FieldPath, bool IsOrphaned,
    string Status, IReadOnlyList<Guid> Mentions, DateTimeOffset CreatedAt);

public sealed record NotificationDto(
    Guid Id, string Type, string Message, Guid? DocumentId, Guid? CommentId, bool IsRead, DateTimeOffset CreatedAt);

/// <summary>Standard page envelope used by list endpoints.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}
