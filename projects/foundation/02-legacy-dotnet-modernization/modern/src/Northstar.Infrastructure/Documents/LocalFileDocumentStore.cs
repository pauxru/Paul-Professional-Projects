using Microsoft.Extensions.Options;
using Northstar.Application.Abstractions;

namespace Northstar.Infrastructure.Documents;

public sealed class LocalFileDocumentStore(IOptions<DocumentStoreOptions> options) : IDocumentStore
{
    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "image/jpeg",
        "image/png"
    };

    public async Task<StoredDocument> SaveAsync(DocumentWrite document, CancellationToken cancellationToken)
    {
        if (!AllowedContentTypes.Contains(document.ContentType))
        {
            throw new ArgumentException("Only PDF, JPEG, and PNG documents are accepted.");
        }

        if (document.Content.Length == 0 || document.Content.Length > options.Value.MaxBytes)
        {
            throw new ArgumentException("Document content is empty or exceeds the configured size limit.");
        }

        var safeName = Path.GetFileName(document.OriginalName);
        var storageKey = Path.Combine(DateTime.UtcNow.ToString("yyyyMM"), $"{Guid.NewGuid():N}-{safeName}");
        var destination = Path.Combine(options.Value.RootPath, storageKey);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllBytesAsync(destination, document.Content, cancellationToken);
        return new StoredDocument(storageKey);
    }
}
