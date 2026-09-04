namespace NotificationPlatform.Application.Unsubscribe;

public sealed record UnsubscribeIssueRequest(string RecipientExternalId, string Category);

public sealed record UnsubscribeIssueResult(string Token, DateTimeOffset ExpiresAt);

public sealed record UnsubscribeResult(bool Success, string? Reason, string? Category);

public interface IUnsubscribeService
{
    Task<UnsubscribeIssueResult> IssueAsync(Guid tenantId, UnsubscribeIssueRequest request, CancellationToken ct);
    Task<UnsubscribeResult> HandleAsync(string token, CancellationToken ct);
}
