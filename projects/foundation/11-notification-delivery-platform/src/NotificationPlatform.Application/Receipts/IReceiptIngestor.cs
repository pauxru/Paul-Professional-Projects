namespace NotificationPlatform.Application.Receipts;

using NotificationPlatform.Domain.Common;

public sealed record ReceiptPayload(Guid NotificationId, string ProviderName, string ProviderMessageId, string Kind, string? Reason);

public sealed record ReceiptIngestResult(bool Accepted, string? Reason);

public interface IReceiptIngestor
{
    Task<ReceiptIngestResult> IngestAsync(string signatureHeader, string timestampHeader, string nonceHeader, string body, CancellationToken cancellationToken);
    /// <summary>
    /// Trusted path used by internal producers (like the delivery pipeline itself)
    /// which have already authenticated their caller.
    /// </summary>
    Task<ReceiptIngestResult> IngestTrustedAsync(ReceiptPayload payload, string nonce, CancellationToken cancellationToken);
}
