using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Northstar.Iga.Application;
using Northstar.Iga.Domain;

namespace Northstar.Iga.Infrastructure;

public sealed partial class IgaService
{
    public async Task<IReadOnlyList<PolicyDefinition>> GetPoliciesAsync(CancellationToken cancellationToken) =>
        await _db.Policies.AsNoTracking()
            .OrderByDescending(x => x.Priority)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);

    public async Task<PolicyDefinition> CreatePolicyAsync(
        CreatePolicyCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Name) || string.IsNullOrWhiteSpace(command.PermissionPattern))
        {
            throw new DomainRuleException("Policy name and permission pattern are required.");
        }

        using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(command.ConditionsJson) ? "{}" : command.ConditionsJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new DomainRuleException("Policy conditions must be a JSON object.");
        }

        PolicyDefinition? policy = null;
        object? before = null;
        if (command.Id is not null)
        {
            policy = await _db.Policies.SingleOrDefaultAsync(x => x.Id == command.Id, cancellationToken);
            if (policy is not null)
            {
                before = new
                {
                    policy.Name,
                    policy.Description,
                    policy.Effect,
                    policy.PermissionPattern,
                    policy.Priority,
                    policy.ConditionsJson,
                    policy.Enabled
                };
            }
        }

        policy ??= new PolicyDefinition { Id = command.Id ?? Guid.NewGuid() };
        policy.Name = command.Name.Trim();
        policy.Description = command.Description.Trim();
        policy.Effect = command.Effect;
        policy.PermissionPattern = command.PermissionPattern.Trim().ToLowerInvariant();
        policy.Priority = command.Priority;
        policy.ConditionsJson = string.IsNullOrWhiteSpace(command.ConditionsJson) ? "{}" : command.ConditionsJson;
        policy.Enabled = command.Enabled;
        if (_db.Entry(policy).State == EntityState.Detached)
        {
            _db.Policies.Add(policy);
        }

        await AppendAuditAsync(actor, before is null ? "policy.created" : "policy.updated", "policy",
            policy.Id.ToString(), correlationId, before, policy, null, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return policy;
    }

    public async Task<AuthorizationDecision> EvaluateAsync(
        AuthorizationEvaluationCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        var user = await RequireUserAsync(command.UserId, cancellationToken);
        var policies = await _db.Policies.AsNoTracking().ToListAsync(cancellationToken);
        var decision = await EvaluateSnapshotAsync(user, command, policies, cancellationToken);
        IgaTelemetry.DecisionLatency.Record(decision.ElapsedMilliseconds);
        await AppendAuditAsync(actor, "authorization.decision", "user", user.Id.ToString(), correlationId, null, decision,
            new { command.Permission, decision.Allowed, decision.DecisivePolicyId }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return decision;
    }

    public async Task<PolicySimulationResult> SimulatePolicyAsync(
        PolicySimulationCommand command,
        CancellationToken cancellationToken)
    {
        using var activity = IgaTelemetry.ActivitySource.StartActivity("policy.simulate");
        var currentPolicies = await _db.Policies.AsNoTracking().ToListAsync(cancellationToken);
        var proposed = new PolicyDefinition
        {
            Id = command.ProposedPolicy.Id ?? Guid.NewGuid(),
            Name = command.ProposedPolicy.Name,
            Description = command.ProposedPolicy.Description,
            Effect = command.ProposedPolicy.Effect,
            PermissionPattern = command.ProposedPolicy.PermissionPattern,
            Priority = command.ProposedPolicy.Priority,
            ConditionsJson = command.ProposedPolicy.ConditionsJson,
            Enabled = command.ProposedPolicy.Enabled
        };
        var changedPolicies = currentPolicies.Where(x => x.Id != proposed.Id).Append(proposed).ToArray();
        var users = await _db.Users.AsNoTracking()
            .Where(x => x.Status == IdentityStatus.Active)
            .OrderBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var gained = new List<Guid>();
        var lost = new List<Guid>();
        var unchangedAllowed = 0;
        var unchangedDenied = 0;
        var evaluationCommand = new AuthorizationEvaluationCommand(
            Guid.Empty,
            command.Permission,
            command.Resource,
            command.Environment);

        foreach (var user in users)
        {
            var before = await EvaluateSnapshotAsync(user, evaluationCommand, currentPolicies, cancellationToken);
            var after = await EvaluateSnapshotAsync(user, evaluationCommand, changedPolicies, cancellationToken);
            if (!before.Allowed && after.Allowed) gained.Add(user.Id);
            else if (before.Allowed && !after.Allowed) lost.Add(user.Id);
            else if (before.Allowed) unchangedAllowed++;
            else unchangedDenied++;
        }

        activity?.SetTag("iga.simulation.identities", users.Count);
        activity?.SetTag("iga.simulation.gained", gained.Count);
        activity?.SetTag("iga.simulation.lost", lost.Count);
        return new PolicySimulationResult(gained, lost, unchangedAllowed, unchangedDenied);
    }

    public async Task<SoDRule> CreateSoDRuleAsync(
        CreateSoDRuleCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (command.EntitlementAId == command.EntitlementBId)
        {
            throw new DomainRuleException("A separation-of-duties rule requires two different entitlements.");
        }

        var found = await _db.Entitlements.CountAsync(
            x => x.Id == command.EntitlementAId || x.Id == command.EntitlementBId,
            cancellationToken);
        if (found != 2)
        {
            throw new KeyNotFoundException("One or both toxic entitlements were not found.");
        }

        var rule = new SoDRule
        {
            Name = command.Name.Trim(),
            EntitlementAId = command.EntitlementAId,
            EntitlementBId = command.EntitlementBId,
            Severity = command.Severity,
            BusinessDescription = command.BusinessDescription.Trim()
        };
        _db.SoDRules.Add(rule);
        await AppendAuditAsync(actor, "sod.rule.created", "sod-rule", rule.Id.ToString(), correlationId, null, rule, null,
            cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return rule;
    }

    public async Task<SoDException> CreateSoDExceptionAsync(
        CreateSoDExceptionCommand command,
        string actor,
        string correlationId,
        CancellationToken cancellationToken)
    {
        if (command.ExpiresAt <= _clock.UtcNow ||
            string.IsNullOrWhiteSpace(command.ApprovedBy) ||
            string.IsNullOrWhiteSpace(command.Justification))
        {
            throw new DomainRuleException("A future expiry, approving authority, and justification are required.");
        }

        if (!await _db.SoDRules.AnyAsync(x => x.Id == command.RuleId, cancellationToken))
        {
            throw new KeyNotFoundException($"SoD rule '{command.RuleId}' was not found.");
        }
        _ = await RequireUserAsync(command.UserId, cancellationToken);

        var exception = new SoDException
        {
            RuleId = command.RuleId,
            UserId = command.UserId,
            ApprovedBy = command.ApprovedBy.Trim(),
            Justification = command.Justification.Trim(),
            CreatedAt = _clock.UtcNow,
            ExpiresAt = command.ExpiresAt
        };
        _db.SoDExceptions.Add(exception);
        await AppendAuditAsync(actor, "sod.exception.approved", "sod-exception", exception.Id.ToString(), correlationId,
            null, exception, new { command.RuleId, command.UserId, command.ExpiresAt }, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        return exception;
    }

    public async Task<IReadOnlyList<SoDViolation>> ScanSoDAsync(CancellationToken cancellationToken)
    {
        var users = await _db.Users.AsNoTracking()
            .Where(x => x.Status == IdentityStatus.Active)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var rules = await _db.SoDRules.AsNoTracking().ToListAsync(cancellationToken);
        var exceptions = await _db.SoDExceptions.AsNoTracking().ToListAsync(cancellationToken);
        var results = new List<SoDViolation>();
        foreach (var userId in users)
        {
            var access = await ResolveDerivationsAsync(userId, cancellationToken);
            results.AddRange(SoDEngine.Detect(
                userId,
                access.Select(x => x.EntitlementId),
                rules,
                exceptions,
                _clock.UtcNow));
        }

        return results;
    }

    private async Task<AuthorizationDecision> EvaluateSnapshotAsync(
        UserIdentity user,
        AuthorizationEvaluationCommand command,
        IEnumerable<PolicyDefinition> policies,
        CancellationToken cancellationToken)
    {
        if (user.Status != IdentityStatus.Active)
        {
            return new AuthorizationDecision(
                false,
                "Deny",
                $"Denied because identity status is '{user.Status}'. Only active identities can receive access.",
                null,
                [],
                [],
                0);
        }

        var derivations = await ResolveDerivationsAsync(user.Id, cancellationToken);
        var resource = command.Resource is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(command.Resource, StringComparer.OrdinalIgnoreCase);
        var environment = command.Environment is null
            ? new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, object?>(command.Environment, StringComparer.OrdinalIgnoreCase);
        var context = new AuthorizationContext(user, command.Permission, resource, environment, _clock.UtcNow);
        return PolicyEngine.Evaluate(context, derivations, policies);
    }
}
