namespace Northstar.Application.Abstractions;

public interface IDocumentStore
{
    Task<StoredDocument> SaveAsync(DocumentWrite document, CancellationToken cancellationToken);
}

public sealed record DocumentWrite(string OriginalName, string ContentType, byte[] Content);

public sealed record StoredDocument(string StorageKey);
