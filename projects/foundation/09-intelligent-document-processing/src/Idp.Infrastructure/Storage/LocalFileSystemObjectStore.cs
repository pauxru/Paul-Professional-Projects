using Idp.Application.Abstractions;
using Idp.Application.Configuration;
using Microsoft.Extensions.Options;

namespace Idp.Infrastructure.Storage;

/// <summary>
/// Local filesystem <see cref="IObjectStore"/>. Files are written under a configured root directory
/// that lives OUTSIDE the web root, so the original (untrusted) document bytes are never served
/// statically. Keys are opaque; a random prefix prevents collisions and path traversal via the
/// suggested name (which is sanitised to its file component).
/// </summary>
public sealed class LocalFileSystemObjectStore : IObjectStore
{
    private readonly string _root;

    public LocalFileSystemObjectStore(IOptions<StorageOptions> options)
    {
        _root = Path.GetFullPath(options.Value.RootPath);
        Directory.CreateDirectory(_root);
    }

    public async Task<string> PutAsync(
        string suggestedName, Stream content, CancellationToken ct = default)
    {
        var safe = Sanitize(suggestedName);
        var key = $"{Guid.NewGuid():N}__{safe}";
        var path = ResolveInsideRoot(key);
        await using var file = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await content.CopyToAsync(file, ct);
        return key;
    }

    public Task<Stream> GetAsync(string key, CancellationToken ct = default)
    {
        var path = ResolveInsideRoot(key);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Object '{key}' not found in store.");
        Stream stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        return Task.FromResult(stream);
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult(File.Exists(ResolveInsideRoot(key)));

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = ResolveInsideRoot(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }

    private static string Sanitize(string name)
    {
        var fileName = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "document";
        foreach (var c in Path.GetInvalidFileNameChars())
            fileName = fileName.Replace(c, '_');
        return fileName;
    }

    /// <summary>Resolve a key to an absolute path and guarantee it stays inside the store root.</summary>
    private string ResolveInsideRoot(string key)
    {
        var candidate = Path.GetFullPath(Path.Combine(_root, Sanitize(key)));
        if (!candidate.StartsWith(_root, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Resolved path escapes the object store root.");
        return candidate;
    }
}
