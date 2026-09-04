namespace Idp.Application.Abstractions;

/// <summary>
/// Content-addressable object store for the original document bytes. The local adapter writes to a
/// configured directory (outside the web root); a documented Azure Blob adapter is described in the
/// architecture docs. Keys are opaque strings owned by the store.
/// </summary>
public interface IObjectStore
{
    Task<string> PutAsync(string suggestedName, Stream content, CancellationToken ct = default);
    Task<Stream> GetAsync(string key, CancellationToken ct = default);
    Task<bool> ExistsAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}
