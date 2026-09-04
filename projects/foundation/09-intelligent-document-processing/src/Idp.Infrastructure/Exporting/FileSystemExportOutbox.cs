using Idp.Application.Configuration;
using Idp.Application.Exporting;
using Microsoft.Extensions.Options;

namespace Idp.Infrastructure.Exporting;

/// <summary>
/// Writes exported payloads to a durable outbox folder and failed payloads to a dead-letter folder.
/// File names are sanitised to their file component to prevent path traversal.
/// </summary>
public sealed class FileSystemExportOutbox : IExportOutbox
{
    private readonly string _outbox;
    private readonly string _deadLetter;

    public FileSystemExportOutbox(IOptions<ExportOptions> options)
    {
        _outbox = Path.GetFullPath(options.Value.OutboxPath);
        _deadLetter = Path.GetFullPath(options.Value.DeadLetterPath);
        Directory.CreateDirectory(_outbox);
        Directory.CreateDirectory(_deadLetter);
    }

    public Task<string> WriteOutboxAsync(string name, string content, CancellationToken ct = default) =>
        WriteAsync(_outbox, name, content, ct);

    public Task<string> WriteDeadLetterAsync(string name, string content, CancellationToken ct = default) =>
        WriteAsync(_deadLetter, name, content, ct);

    private static async Task<string> WriteAsync(
        string root, string name, string content, CancellationToken ct)
    {
        var safe = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(safe)) safe = $"export-{Guid.NewGuid():N}";
        var path = Path.Combine(root, safe);
        await File.WriteAllTextAsync(path, content, ct);
        return path;
    }
}
