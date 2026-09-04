namespace AgentPlatform.Application.Abstractions;

/// <summary>
/// The authenticated principal on whose behalf a run executes. Scopes gate which tools may be
/// invoked; the tenant scopes all data access and budgets.
/// </summary>
public sealed record AgentCaller(string UserId, string TenantId, IReadOnlySet<string> Scopes)
{
    public bool HasScope(string scope) => Scopes.Contains(scope);

    public static AgentCaller System(string tenantId) =>
        new("system", tenantId, new HashSet<string>(StringComparer.Ordinal) { "agents:run", "agents:approve", "agents:admin" });
}
