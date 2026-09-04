namespace ZeroTrust.Domain.Identity;

/// <summary>
/// Deterministic evaluation of an authorization request. The application will use this
/// as the shape returned from the "authz-evaluate" endpoint so callers can see the
/// deciding requirement (explainable authorization).
/// </summary>
public sealed class AuthorizationDecision
{
    public bool Allowed { get; init; }
    public string Policy { get; init; } = default!;
    public string DecidingRequirement { get; init; } = default!;
    public string Reason { get; init; } = default!;
    public string PrincipalSubject { get; init; } = default!;
    public string Action { get; init; } = default!;
    public string Resource { get; init; } = default!;

    public static AuthorizationDecision Allow(string policy, string principal, string action, string resource, string reason) =>
        new() { Allowed = true, Policy = policy, DecidingRequirement = "policy.satisfied",
                Reason = reason, PrincipalSubject = principal, Action = action, Resource = resource };

    public static AuthorizationDecision Deny(string policy, string principal, string action, string resource,
        string decidingRequirement, string reason) =>
        new() { Allowed = false, Policy = policy, DecidingRequirement = decidingRequirement,
                Reason = reason, PrincipalSubject = principal, Action = action, Resource = resource };
}
