namespace JobScheduler.Api.Auth;

/// <summary>Policy and scope names for authorising scheduler operations.</summary>
public static class AuthConstants
{
    public const string ScopeClaim = "scope";

    public const string ScopeRead = "jobs:read";
    public const string ScopeTrigger = "jobs:trigger";
    public const string ScopeManage = "jobs:manage";
    public const string ScopeAdmin = "jobs:admin";

    public const string PolicyRead = "jobs:read";
    public const string PolicyTrigger = "jobs:trigger";
    public const string PolicyManage = "jobs:manage";
    public const string PolicyAdmin = "jobs:admin";

    public static readonly IReadOnlyList<string> AllScopes =
        [ScopeRead, ScopeTrigger, ScopeManage, ScopeAdmin];
}
