namespace NotificationPlatform.Api.Auth;

using System.Security.Claims;

public static class Policies
{
    public const string SendNotifications = "notifications:send";
    public const string ManageTemplates = "templates:manage";
    public const string ManagePreferences = "preferences:manage";
    public const string ManageSuppressions = "suppressions:manage";
    public const string ManageDlq = "dlq:manage";
    public const string ViewAnalytics = "analytics:view";
    public const string IngestReceipts = "receipts:ingest";
}

public static class ClaimNames
{
    public const string TenantId = "tenant_id";
    public const string Scope = "scope";
}

public static class ClaimsPrincipalExtensions
{
    public static Guid? TenantId(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(ClaimNames.TenantId);
        return Guid.TryParse(raw, out var id) ? id : null;
    }
}
