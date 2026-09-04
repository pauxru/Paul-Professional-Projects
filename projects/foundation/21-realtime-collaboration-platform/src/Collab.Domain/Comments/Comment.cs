using Collab.Domain.Abstractions;

namespace Collab.Domain.Comments;

public enum CommentStatus
{
    Open = 0,
    Resolved = 1
}

public enum AnchorKind
{
    /// <summary>Anchored to a character range in a text document.</summary>
    TextRange = 0,
    /// <summary>Anchored to a field of a structured document.</summary>
    Field = 1
}

/// <summary>
/// A comment anchored to a document. Root comments start a thread (<see cref="ThreadId"/> == own
/// <see cref="Id"/>); replies share the root's thread id. Text-anchored comments carry a character
/// range that is rebased as the document changes; when the anchored text is deleted the comment is
/// flagged <see cref="IsOrphaned"/> rather than lost.
/// </summary>
public sealed class Comment
{
    private Comment() { }

    private Comment(
        Guid documentId,
        Guid threadId,
        Guid? parentCommentId,
        Guid authorUserId,
        string body,
        AnchorKind anchorKind,
        int anchorStart,
        int anchorEnd,
        string? fieldPath,
        IEnumerable<Guid> mentions,
        IClock clock)
    {
        Id = Guid.NewGuid();
        DocumentId = documentId;
        ThreadId = threadId == Guid.Empty ? Id : threadId;
        ParentCommentId = parentCommentId;
        AuthorUserId = authorUserId;
        Body = body;
        AnchorKind = anchorKind;
        AnchorStart = anchorStart;
        AnchorEnd = anchorEnd;
        FieldPath = fieldPath;
        MentionsCsv = string.Join(',', mentions.Distinct());
        Status = CommentStatus.Open;
        CreatedAt = UpdatedAt = clock.UtcNow;
    }

    public static Comment CreateTextComment(
        Guid documentId, Guid authorUserId, string body, int start, int end, IEnumerable<Guid> mentions, IClock clock) =>
        new(documentId, Guid.Empty, null, authorUserId, body, AnchorKind.TextRange, start, end, null, mentions, clock);

    public static Comment CreateFieldComment(
        Guid documentId, Guid authorUserId, string body, string fieldPath, IEnumerable<Guid> mentions, IClock clock) =>
        new(documentId, Guid.Empty, null, authorUserId, body, AnchorKind.Field, 0, 0, fieldPath, mentions, clock);

    public Comment Reply(Guid authorUserId, string body, IEnumerable<Guid> mentions, IClock clock) =>
        new(DocumentId, ThreadId, Id, authorUserId, body, AnchorKind, AnchorStart, AnchorEnd, FieldPath, mentions, clock);

    public Guid Id { get; private set; }
    public Guid DocumentId { get; private set; }
    public Guid ThreadId { get; private set; }
    public Guid? ParentCommentId { get; private set; }
    public Guid AuthorUserId { get; private set; }
    public string Body { get; private set; } = null!;
    public AnchorKind AnchorKind { get; private set; }
    public int AnchorStart { get; private set; }
    public int AnchorEnd { get; private set; }
    public string? FieldPath { get; private set; }
    public bool IsOrphaned { get; private set; }
    public CommentStatus Status { get; private set; }
    public string MentionsCsv { get; private set; } = string.Empty;
    public Guid? ResolvedByUserId { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public IReadOnlyList<Guid> Mentions =>
        string.IsNullOrEmpty(MentionsCsv)
            ? []
            : MentionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToArray();

    public TextAnchor Anchor => new(AnchorStart, AnchorEnd, IsOrphaned);

    /// <summary>Apply a rebased anchor (after the underlying text changed).</summary>
    public void UpdateAnchor(TextAnchor anchor, IClock clock)
    {
        if (AnchorKind != AnchorKind.TextRange) return;
        AnchorStart = anchor.Start;
        AnchorEnd = anchor.End;
        IsOrphaned = anchor.Orphaned;
        UpdatedAt = clock.UtcNow;
    }

    public void Resolve(Guid byUserId, IClock clock)
    {
        Status = CommentStatus.Resolved;
        ResolvedByUserId = byUserId;
        ResolvedAt = clock.UtcNow;
        UpdatedAt = clock.UtcNow;
    }

    public void Reopen(IClock clock)
    {
        Status = CommentStatus.Open;
        ResolvedByUserId = null;
        ResolvedAt = null;
        UpdatedAt = clock.UtcNow;
    }
}
