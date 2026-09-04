namespace RagAssistant.Domain.Documents;

public sealed class Document
{
    private readonly List<DocumentChunk> _chunks = [];

    private Document()
    {
        Id = Guid.Empty;
        Title = string.Empty;
        Source = string.Empty;
        ContentHash = string.Empty;
        Content = string.Empty;
        Acl = new AccessControlList();
    }

    public Document(Guid id, string title, string source, string content, string contentHash, AccessControlList acl, DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Document id must be provided.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Document title must be provided.", nameof(title));
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Document source must be provided.", nameof(source));
        }

        Id = id;
        Title = title.Trim();
        Source = source.Trim();
        Content = content ?? string.Empty;
        ContentHash = contentHash ?? string.Empty;
        Acl = CloneAcl(acl ?? throw new ArgumentNullException(nameof(acl)));
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        Version = 1;
    }

    public Guid Id { get; private set; }
    public string Title { get; private set; }
    public string Source { get; private set; }
    public string Content { get; private set; }
    public string ContentHash { get; private set; }
    public AccessControlList Acl { get; private set; }
    public int Version { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyList<DocumentChunk> Chunks => _chunks;

    public void UpdateContent(string content, string contentHash, AccessControlList acl, DateTimeOffset updatedAt)
    {
        Content = content ?? string.Empty;
        ContentHash = contentHash ?? string.Empty;
        Acl = CloneAcl(acl ?? throw new ArgumentNullException(nameof(acl)));
        UpdatedAt = updatedAt;
        Version += 1;
        _chunks.Clear();
    }

    public void ReplaceChunks(IEnumerable<DocumentChunk> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        _chunks.Clear();
        _chunks.AddRange(chunks);
    }

    private static AccessControlList CloneAcl(AccessControlList acl) =>
        new(acl.Roles.ToArray(), acl.Departments.ToArray(), acl.Classification);
}
