namespace Idp.Application.Exporting;

/// <summary>
/// Destination for exported document payloads: a durable outbox folder and a dead-letter folder.
/// The local adapter writes files; a production adapter might write to blob storage or a queue.
/// </summary>
public interface IExportOutbox
{
    Task<string> WriteOutboxAsync(string name, string content, CancellationToken ct = default);
    Task<string> WriteDeadLetterAsync(string name, string content, CancellationToken ct = default);
}
