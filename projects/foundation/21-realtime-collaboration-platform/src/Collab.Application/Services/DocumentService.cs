using Collab.Application.Abstractions;
using Collab.Application.Contracts;
using Collab.Domain.Abstractions;
using Collab.Domain.Authorization;
using Collab.Domain.Diffing;
using Collab.Domain.Documents;

namespace Collab.Application.Services;

/// <summary>
/// REST-facing document operations: lifecycle, content reads, history, named versions, diffing,
/// time-travel and restore. Content is always a read-model rebuilt from snapshot + operation log,
/// keeping the persisted document row free of content and the log as the single source of truth.
/// </summary>
public sealed class DocumentService(
    IDocumentRepository documents,
    IOperationLogRepository opLog,
    ISnapshotRepository snapshots,
    INamedVersionRepository versions,
    IAccessControl access,
    CollaborationService collaboration,
    IAuditLog audit,
    IClock clock,
    IUnitOfWork uow)
{
    public async Task<DocumentDto> CreateAsync(Guid userId, CreateDocumentRequest request, CancellationToken ct = default)
    {
        var title = (request.Title ?? string.Empty).Trim();
        if (title.Length is 0 or > 300)
            throw new ValidationAppException(nameof(request.Title), "Title must be between 1 and 300 characters.");
        var type = ParseType(request.Type);

        var decision = await access.ForWorkspaceAsync(userId, request.WorkspaceId, Capability.Edit, ct);
        if (!decision.Allowed) throw new ForbiddenException(decision.Reason);

        var document = new Document(request.WorkspaceId, title, type, userId, clock);
        documents.Add(document);
        audit.Record("document.created", "document", document.Id.ToString(), userId, request.WorkspaceId, document.Id,
            details: $"type={type};title={title}");
        await uow.SaveChangesAsync(ct);
        return Map(document);
    }

    public async Task<PagedResult<DocumentDto>> ListAsync(Guid userId, Guid workspaceId, int page, int pageSize, CancellationToken ct = default)
    {
        var decision = await access.ForWorkspaceAsync(userId, workspaceId, Capability.View, ct);
        if (!decision.Allowed) throw new ForbiddenException(decision.Reason);

        (page, pageSize) = Normalize(page, pageSize);
        var total = await documents.CountByWorkspaceAsync(workspaceId, ct);
        var items = await documents.ListByWorkspaceAsync(workspaceId, (page - 1) * pageSize, pageSize, ct);
        return new PagedResult<DocumentDto>(items.Select(Map).ToArray(), page, pageSize, total);
    }

    public async Task<DocumentContentDto> GetContentAsync(Guid userId, Guid documentId, CancellationToken ct = default)
    {
        var document = await AuthorizeDocumentAsync(userId, documentId, Capability.View, ct);
        var content = await RenderAtAsync(document, document.CurrentSequence, ct);
        return new DocumentContentDto(document.Id, document.Title, document.Type.ToString(), document.CurrentSequence, content);
    }

    public async Task<DocumentContentDto> TimeTravelAsync(Guid userId, Guid documentId, long atSequence, CancellationToken ct = default)
    {
        var document = await AuthorizeDocumentAsync(userId, documentId, Capability.View, ct);
        if (atSequence < 0 || atSequence > document.CurrentSequence)
            throw new ValidationAppException("atSequence", "Sequence is out of range.");
        var content = await RenderAtAsync(document, atSequence, ct);
        return new DocumentContentDto(document.Id, document.Title, document.Type.ToString(), atSequence, content);
    }

    public async Task<PagedResult<OperationLogDto>> GetHistoryAsync(Guid userId, Guid documentId, int page, int pageSize, CancellationToken ct = default)
    {
        await AuthorizeDocumentAsync(userId, documentId, Capability.View, ct);
        (page, pageSize) = Normalize(page, pageSize);
        var total = await opLog.CountAsync(documentId, ct);
        var entries = await opLog.ListAsync(documentId, (page - 1) * pageSize, pageSize, ct);
        var items = entries
            .Select(e => new OperationLogDto(e.ServerSequence, e.AuthorUserId, e.Kind.ToString(), e.CreatedAt))
            .ToArray();
        return new PagedResult<OperationLogDto>(items, page, pageSize, total);
    }

    public async Task<NamedVersionDto> CreateNamedVersionAsync(Guid userId, Guid documentId, CreateNamedVersionRequest request, CancellationToken ct = default)
    {
        var document = await AuthorizeDocumentAsync(userId, documentId, Capability.Edit, ct);
        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 200)
            throw new ValidationAppException(nameof(request.Name), "Name must be between 1 and 200 characters.");

        var version = new NamedVersion(document.Id, name, document.CurrentSequence, userId, clock);
        versions.Add(version);
        audit.Record("document.version_named", "document", document.Id.ToString(), userId, document.WorkspaceId, document.Id,
            details: $"name={name};atSequence={document.CurrentSequence}");
        await uow.SaveChangesAsync(ct);
        return new NamedVersionDto(version.Id, version.Name, version.AtSequence, version.CreatedAt);
    }

    public async Task<IReadOnlyList<NamedVersionDto>> ListVersionsAsync(Guid userId, Guid documentId, CancellationToken ct = default)
    {
        await AuthorizeDocumentAsync(userId, documentId, Capability.View, ct);
        var list = await versions.ListAsync(documentId, ct);
        return list.Select(v => new NamedVersionDto(v.Id, v.Name, v.AtSequence, v.CreatedAt)).ToArray();
    }

    public async Task<DiffResultDto> DiffAsync(Guid userId, Guid documentId, long fromSequence, long toSequence, CancellationToken ct = default)
    {
        var document = await AuthorizeDocumentAsync(userId, documentId, Capability.View, ct);
        if (fromSequence < 0 || fromSequence > document.CurrentSequence || toSequence < 0 || toSequence > document.CurrentSequence)
            throw new ValidationAppException("sequence", "A diff endpoint sequence is out of range.");

        var fromContent = await RenderAtAsync(document, fromSequence, ct);
        var toContent = await RenderAtAsync(document, toSequence, ct);
        var segments = DiffEngine.DiffLines(fromContent, toContent)
            .Select(s => new DiffSegmentDto(s.Kind.ToString(), s.Text))
            .ToArray();
        return new DiffResultDto(fromSequence, toSequence, segments);
    }

    public async Task<DocumentContentDto> RestoreAsync(Guid userId, Guid documentId, RestoreRequest request, CancellationToken ct = default)
    {
        var result = await collaboration.RestoreAsync(userId, documentId, request.ToSequence, ct);
        if (!result.Accepted)
            throw MapRejection(result.Rejection!);

        var document = await documents.GetAsync(documentId, ct)
            ?? throw new NotFoundException("Document not found.");
        var content = await RenderAtAsync(document, document.CurrentSequence, ct);
        return new DocumentContentDto(document.Id, document.Title, document.Type.ToString(), document.CurrentSequence, content);
    }

    private async Task<string> RenderAtAsync(Document document, long sequence, CancellationToken ct)
    {
        var snapshot = await snapshots.GetLatestAtOrBeforeAsync(document.Id, sequence, ct);
        var baseSequence = snapshot?.AtSequence ?? 0;
        var tail = (await opLog.GetUpToAsync(document.Id, sequence, ct))
            .Where(e => e.ServerSequence > baseSequence)
            .ToList();
        return document.Type == DocumentType.Text
            ? DocumentReplay.RenderText(snapshot, tail)
            : DocumentReplay.RenderStructured(snapshot, tail);
    }

    private async Task<Document> AuthorizeDocumentAsync(Guid userId, Guid documentId, Capability capability, CancellationToken ct)
    {
        var document = await documents.GetAsync(documentId, ct)
            ?? throw new NotFoundException("Document not found.");
        var decision = await access.ForWorkspaceAsync(userId, document.WorkspaceId, capability, ct);
        if (!decision.Allowed) throw new ForbiddenException(decision.Reason);
        return document;
    }

    private static AppException MapRejection(OperationRejected rejection) => rejection.Code switch
    {
        "forbidden" => new ForbiddenException(rejection.Reason),
        "not_found" => new NotFoundException(rejection.Reason),
        "invalid_sequence" => new ValidationAppException("toSequence", rejection.Reason),
        "no_changes" => new ConflictException(rejection.Reason),
        _ => new DomainRuleException(rejection.Reason)
    };

    private static DocumentType ParseType(string type) => (type ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "text" => DocumentType.Text,
        "structured" => DocumentType.Structured,
        _ => throw new ValidationAppException("type", "Type must be 'text' or 'structured'.")
    };

    private static (int Page, int PageSize) Normalize(int page, int pageSize) =>
        (page < 1 ? 1 : page, pageSize is < 1 or > 200 ? 50 : pageSize);

    private static DocumentDto Map(Document d) =>
        new(d.Id, d.WorkspaceId, d.Title, d.Type.ToString(), d.CurrentSequence, d.CreatedAt, d.UpdatedAt);
}
