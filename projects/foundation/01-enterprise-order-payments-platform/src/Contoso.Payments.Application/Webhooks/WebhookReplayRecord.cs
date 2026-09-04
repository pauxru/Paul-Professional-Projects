namespace Contoso.Payments.Application.Webhooks;

public sealed class WebhookReplayRecord
{
    public string SignatureHash { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAtUtc { get; set; }
}
