using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using ZeroTrust.Application.Abstractions;
using ZeroTrust.Domain.Identity;
using ZeroTrust.Infrastructure.Persistence;

namespace ZeroTrust.Api.Authorization;

public sealed record AuthzEvaluationRequest(string Token, string Action, string Resource);

/// <summary>
/// Explainable authorization: given a token + intended action + resource, returns which
/// requirement fired and the reason. Useful in debugging, in an admin console, and — most
/// importantly — as the "policy decision point" pattern for external policy evaluation.
/// </summary>
public sealed class AuthorizationExplainer
{
    private readonly ITokenValidator _validator;
    private readonly ZeroTrustDbContext _db;

    public AuthorizationExplainer(ITokenValidator validator, ZeroTrustDbContext db)
    {
        _validator = validator;
        _db = db;
    }

    public async Task<AuthorizationDecision> EvaluateAsync(AuthzEvaluationRequest req, CancellationToken ct)
    {
        var jwt = new JwtSecurityTokenHandler().CanReadToken(req.Token)
            ? new JwtSecurityTokenHandler().ReadJwtToken(req.Token)
            : null;
        var audience = jwt?.Audiences.FirstOrDefault() ?? Audience.Customer;
        var validation = await _validator.ValidateAsync(req.Token, audience, ct);

        if (!validation.IsValid)
        {
            return AuthorizationDecision.Deny(policy: MapPolicy(req.Action),
                principal: validation.Subject ?? "?", action: req.Action, resource: req.Resource,
                decidingRequirement: "authentication.failed",
                reason: validation.FailureReason ?? "unknown");
        }

        var (policy, decision) = req.Action switch
        {
            "customer:read-accounts" => await EvaluateCustomerReadAccounts(validation, req.Resource, ct),
            "customer:read-account" => await EvaluateCustomerReadAccount(validation, req.Resource, ct),
            "partner:initiate-payment" => EvaluatePartnerInitiate(validation),
            "partner:read-payment" => EvaluatePartnerReadPayment(validation),
            "admin:audit" => EvaluateAdminAudit(validation),
            "admin:users" => EvaluateAdminUsers(validation),
            _ => (MapPolicy(req.Action), AuthorizationDecision.Deny(MapPolicy(req.Action),
                    validation.Subject ?? "?", req.Action, req.Resource,
                    "unknown.action", $"action '{req.Action}' is not modelled"))
        };
        _ = policy;
        return decision;
    }

    private static string MapPolicy(string action) => action switch
    {
        "customer:read-accounts" or "customer:read-account" => "customer.read",
        "partner:initiate-payment" => "partner.payments.initiate",
        "partner:read-payment" => "partner.payments.read",
        "admin:audit" => "admin.audit",
        "admin:users" => "admin.users",
        _ => "unknown"
    };

    private static (string policy, AuthorizationDecision decision) EvaluatePartnerInitiate(TokenValidationOutcome v)
    {
        const string policy = "partner.payments.initiate";
        if (!string.Equals(v.Audience, Audience.Partner, StringComparison.Ordinal))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "partner:initiate-payment", "-",
                "audience.mismatch", $"token audience '{v.Audience}' != '{Audience.Partner}'"));
        if (!v.Scopes.Contains(Scope.PartnerPaymentsInitiate))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "partner:initiate-payment", "-",
                "scope.missing", $"required scope '{Scope.PartnerPaymentsInitiate}' not present"));
        return (policy, AuthorizationDecision.Allow(policy, v.Subject!, "partner:initiate-payment", "-",
            "audience + scope satisfied"));
    }

    private static (string policy, AuthorizationDecision decision) EvaluatePartnerReadPayment(TokenValidationOutcome v)
    {
        const string policy = "partner.payments.read";
        if (!string.Equals(v.Audience, Audience.Partner, StringComparison.Ordinal))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "partner:read-payment", "-",
                "audience.mismatch", $"token audience '{v.Audience}' != '{Audience.Partner}'"));
        if (!v.Scopes.Contains(Scope.PartnerPaymentsRead))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "partner:read-payment", "-",
                "scope.missing", $"required scope '{Scope.PartnerPaymentsRead}' not present"));
        return (policy, AuthorizationDecision.Allow(policy, v.Subject!, "partner:read-payment", "-", "ok"));
    }

    private static (string policy, AuthorizationDecision decision) EvaluateAdminAudit(TokenValidationOutcome v)
    {
        const string policy = "admin.audit";
        if (!string.Equals(v.Audience, Audience.Admin, StringComparison.Ordinal))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "admin:audit", "-",
                "audience.mismatch", $"token audience '{v.Audience}' != '{Audience.Admin}'"));
        if (!v.Scopes.Contains(Scope.AdminAudit))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "admin:audit", "-",
                "scope.missing", $"required scope '{Scope.AdminAudit}' not present"));
        if (v.Amr != "mfa" || v.Acr != "urn:ntsf:acr:step-up")
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "admin:audit", "-",
                "step_up.required", "admin surface requires amr='mfa' and acr='urn:ntsf:acr:step-up'"));
        return (policy, AuthorizationDecision.Allow(policy, v.Subject!, "admin:audit", "-", "ok"));
    }

    private static (string policy, AuthorizationDecision decision) EvaluateAdminUsers(TokenValidationOutcome v)
    {
        const string policy = "admin.users";
        if (!string.Equals(v.Audience, Audience.Admin, StringComparison.Ordinal))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "admin:users", "-",
                "audience.mismatch", $"token audience '{v.Audience}' != '{Audience.Admin}'"));
        if (!v.Scopes.Contains(Scope.AdminUsers))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "admin:users", "-",
                "scope.missing", $"required scope '{Scope.AdminUsers}' not present"));
        if (!v.Roles.Contains("admin"))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "admin:users", "-",
                "role.missing", "role 'admin' required"));
        if (v.Amr != "mfa" || v.Acr != "urn:ntsf:acr:step-up")
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "admin:users", "-",
                "step_up.required", "admin surface requires amr='mfa' and acr='urn:ntsf:acr:step-up'"));
        return (policy, AuthorizationDecision.Allow(policy, v.Subject!, "admin:users", "-", "ok"));
    }

    private async Task<(string policy, AuthorizationDecision decision)> EvaluateCustomerReadAccounts(TokenValidationOutcome v, string resource, CancellationToken ct)
    {
        const string policy = "customer.read";
        if (!string.Equals(v.Audience, Audience.Customer, StringComparison.Ordinal))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "customer:read-accounts", resource,
                "audience.mismatch", $"token audience '{v.Audience}' != '{Audience.Customer}'"));
        if (!v.Scopes.Contains(Scope.CustomerRead))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "customer:read-accounts", resource,
                "scope.missing", $"required scope '{Scope.CustomerRead}' not present"));
        _ = await Task.FromResult(0);
        return (policy, AuthorizationDecision.Allow(policy, v.Subject!, "customer:read-accounts", resource, "ok"));
    }

    private async Task<(string policy, AuthorizationDecision decision)> EvaluateCustomerReadAccount(TokenValidationOutcome v, string resource, CancellationToken ct)
    {
        const string policy = "customer.read";
        if (!string.Equals(v.Audience, Audience.Customer, StringComparison.Ordinal))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "customer:read-account", resource,
                "audience.mismatch", $"token audience '{v.Audience}' != '{Audience.Customer}'"));
        if (!v.Scopes.Contains(Scope.CustomerRead))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "customer:read-account", resource,
                "scope.missing", $"required scope '{Scope.CustomerRead}' not present"));
        if (!Guid.TryParse(resource, out var accountId))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "customer:read-account", resource,
                "resource.invalid", "resource must be a Guid account id"));
        var account = await _db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null)
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "customer:read-account", resource,
                "resource.not_found", "account not found"));
        if (!account.IsOwnedBy(v.Subject!))
            return (policy, AuthorizationDecision.Deny(policy, v.Subject!, "customer:read-account", resource,
                "ownership.denied", $"account is not owned by '{v.Subject}'"));
        return (policy, AuthorizationDecision.Allow(policy, v.Subject!, "customer:read-account", resource, "ok"));
    }
}
