namespace Contoso.Payments.Application.Abstractions;

/// <summary>Records webhook signatures we have already accepted, for replay protection.</summary>
public interface IWebhookReplayStore
{
    Task<bool> SeenAsync(string signatureHash, CancellationToken ct);
    Task RecordAsync(string signatureHash, DateTimeOffset receivedAtUtc, CancellationToken ct);
}
