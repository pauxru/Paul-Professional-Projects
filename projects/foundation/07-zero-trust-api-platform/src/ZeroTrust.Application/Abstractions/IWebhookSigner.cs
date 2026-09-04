namespace ZeroTrust.Application.Abstractions;

public sealed record WebhookVerificationResult(bool IsValid, string? Reason);

public interface IWebhookSigner
{
    string SchemeVersion { get; }
    string Sign(string rawBody, long unixTimestamp, string nonce, string secret);
}

public interface IWebhookVerifier
{
    WebhookVerificationResult Verify(string rawBody, string signatureHeader, string? timestampHeader,
        string? nonceHeader, string secret, DateTime nowUtc, TimeSpan tolerance);
}
