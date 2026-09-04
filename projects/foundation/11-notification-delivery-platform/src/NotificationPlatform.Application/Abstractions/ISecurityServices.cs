namespace NotificationPlatform.Application.Abstractions;

public interface IUnsubscribeTokenService
{
    string Issue(Guid tenantId, Guid recipientId, string category, DateTimeOffset issuedAt, TimeSpan lifetime);
    UnsubscribeTokenResult Validate(string token, DateTimeOffset now);
}

public sealed record UnsubscribeTokenResult(bool IsValid, string? Reason, Guid TenantId, Guid RecipientId, string Category);

public interface IWebhookSignatureService
{
    string Sign(string body, DateTimeOffset now);
    SignatureValidationResult Verify(string signatureHeader, string timestampHeader, string body, DateTimeOffset now, TimeSpan tolerance);
}

public sealed record SignatureValidationResult(bool IsValid, string? Reason);
