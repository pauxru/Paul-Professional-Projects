using Northstar.Iga.Domain;

namespace Northstar.Iga.UnitTests;

public sealed class PolicyEngineTests
{
    [Fact]
    public void Evaluate_DerivedPermissionAndNoPolicies_Allows()
    {
        var decision = Evaluate([Derivation()], []);
        Assert.True(decision.Allowed);
        Assert.Contains("effective RBAC", decision.Explanation);
    }

    [Fact]
    public void Evaluate_NoGrantOrAllow_DefaultDenies()
    {
        var decision = Evaluate([], []);
        Assert.False(decision.Allowed);
        Assert.Contains("default", decision.Explanation);
    }

    [Fact]
    public void Evaluate_ExplicitDenyAndAllow_DenyAlwaysWins()
    {
        var allow = Policy("Allow Finance", PolicyEffect.Allow, 100, "{}");
        var deny = Policy("Deny untrusted", PolicyEffect.Deny, 1, """{"environment":{"deviceTrust":"Untrusted"}}""");

        var decision = Evaluate([Derivation()], [allow, deny], environment: new() { ["deviceTrust"] = "Untrusted" });

        Assert.False(decision.Allowed);
        Assert.Equal(deny.Id, decision.DecisivePolicyId);
    }

    [Fact]
    public void Evaluate_MatchingAllowWithoutRbac_AllowsAbacGrant()
    {
        var policy = Policy("Finance allow", PolicyEffect.Allow, 50, """{"subject":{"department":"Finance"}}""");
        var decision = Evaluate([], [policy]);
        Assert.True(decision.Allowed);
        Assert.Equal(policy.Id, decision.DecisivePolicyId);
    }

    [Fact]
    public void Evaluate_MultipleAllows_HigherPriorityIsDecisive()
    {
        var low = Policy("Low", PolicyEffect.Allow, 5, "{}");
        var high = Policy("High", PolicyEffect.Allow, 50, "{}");
        var decision = Evaluate([], [low, high]);
        Assert.Equal(high.Id, decision.DecisivePolicyId);
    }

    [Fact]
    public void Evaluate_SamePriority_MoreSpecificPolicyIsDecisive()
    {
        var broad = Policy("Broad", PolicyEffect.Allow, 10, "{}", "app:*");
        var specific = Policy("Specific", PolicyEffect.Allow, 10, """{"subject":{"department":"Finance"}}""");
        var decision = Evaluate([], [broad, specific]);
        Assert.Equal(specific.Id, decision.DecisivePolicyId);
    }

    [Fact]
    public void Evaluate_SubjectEmploymentTypeCondition_Matches()
    {
        var policy = Policy("Employee", PolicyEffect.Allow, 10,
            """{"subject":{"employmentType":"Employee","clearance":">=3"}}""");
        Assert.True(Evaluate([], [policy]).Allowed);
    }

    [Fact]
    public void Evaluate_ResourceOwnerVariable_MatchesSubject()
    {
        var user = User();
        var policy = Policy("Owner", PolicyEffect.Allow, 10, """{"resource":{"owner":"${subject.id}"}}""");
        var decision = Evaluate([], [policy], user: user, resource: new() { ["owner"] = user.Id.ToString() });
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Evaluate_ResourceCostCentreVariable_MatchesSubject()
    {
        var policy = Policy("Cost centre", PolicyEffect.Allow, 10,
            """{"resource":{"costCentre":"${subject.costCentre}"}}""");
        var decision = Evaluate([], [policy], resource: new() { ["costCentre"] = "CC-FIN" });
        Assert.True(decision.Allowed);
    }

    [Fact]
    public void Evaluate_TimeOfDayInsideWindow_Matches()
    {
        var policy = Policy("Business hours", PolicyEffect.Allow, 10,
            """{"environment":{"timeOfDay":"08:00-18:00"}}""");
        Assert.True(Evaluate([], [policy], now: new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero)).Allowed);
    }

    [Fact]
    public void Evaluate_TimeOfDayOutsideWindow_DoesNotMatch()
    {
        var policy = Policy("Business hours", PolicyEffect.Allow, 10,
            """{"environment":{"timeOfDay":"08:00-18:00"}}""");
        Assert.False(Evaluate([], [policy], now: new DateTimeOffset(2026, 9, 3, 22, 0, 0, TimeSpan.Zero)).Allowed);
    }

    [Fact]
    public void Evaluate_OvernightTimeWindow_MatchesAfterMidnight()
    {
        var policy = Policy("Night shift", PolicyEffect.Allow, 10,
            """{"environment":{"timeOfDay":"22:00-05:00"}}""");
        Assert.True(Evaluate([], [policy], now: new DateTimeOffset(2026, 9, 3, 2, 0, 0, TimeSpan.Zero)).Allowed);
    }

    [Fact]
    public void Evaluate_NetworkZoneCondition_Matches()
    {
        var policy = Policy("Corporate zone", PolicyEffect.Allow, 10,
            """{"environment":{"networkZone":"Corporate"}}""");
        Assert.True(Evaluate([], [policy], environment: new() { ["networkZone"] = "Corporate" }).Allowed);
    }

    [Fact]
    public void Evaluate_IpWildcardCondition_Matches()
    {
        var policy = Policy("Office IP", PolicyEffect.Allow, 10,
            """{"environment":{"ipAddress":"10.20.*"}}""");
        Assert.True(Evaluate([], [policy], environment: new() { ["ipAddress"] = "10.20.30.40" }).Allowed);
    }

    [Fact]
    public void Evaluate_MfaNumericCondition_Matches()
    {
        var policy = Policy("Strong MFA", PolicyEffect.Allow, 10,
            """{"environment":{"mfaLevel":">=2"}}""");
        Assert.True(Evaluate([], [policy], environment: new() { ["mfaLevel"] = 3 }).Allowed);
    }

    [Fact]
    public void Evaluate_DeviceTrustCondition_MismatchDeniesByDefault()
    {
        var policy = Policy("Trusted device", PolicyEffect.Allow, 10,
            """{"environment":{"deviceTrust":"Trusted"}}""");
        Assert.False(Evaluate([], [policy], environment: new() { ["deviceTrust"] = "Untrusted" }).Allowed);
    }

    [Theory]
    [InlineData("app:finance/*", "app:finance/payment:approve", true)]
    [InlineData("app:finance/vendor:view", "app:finance/vendor:view", true)]
    [InlineData("app:finance/vendor:view", "app:finance/vendor:edit", false)]
    public void PermissionMatches_WildcardAndExactPatterns_Work(string pattern, string permission, bool expected)
    {
        Assert.Equal(expected, PolicyEngine.PermissionMatches(pattern, permission));
    }

    [Fact]
    public void Evaluate_NonMatchingPolicy_ExplanationShowsItWasEvaluated()
    {
        var policy = Policy("Other app", PolicyEffect.Allow, 10, "{}", "app:crm/*");
        var decision = Evaluate([], [policy]);
        var trace = Assert.Single(decision.PoliciesEvaluated);
        Assert.False(trace.PermissionMatched);
        Assert.Contains(trace.Reasons, x => x.Contains("did not match", StringComparison.Ordinal));
    }

    [Fact]
    public void Evaluate_ExpiredDerivation_IsIgnored()
    {
        var expired = Derivation() with { ExpiresAt = new DateTimeOffset(2026, 9, 3, 9, 0, 0, TimeSpan.Zero) };
        var decision = Evaluate([expired], [], now: new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero));
        Assert.False(decision.Allowed);
    }

    private static AuthorizationDecision Evaluate(
        IReadOnlyCollection<AccessDerivation> derivations,
        IReadOnlyCollection<PolicyDefinition> policies,
        UserIdentity? user = null,
        Dictionary<string, object?>? resource = null,
        Dictionary<string, object?>? environment = null,
        DateTimeOffset? now = null) =>
        PolicyEngine.Evaluate(
            new AuthorizationContext(
                user ?? User(),
                "app:finance/vendor:create",
                resource ?? new Dictionary<string, object?>(),
                environment ?? new Dictionary<string, object?>(),
                now ?? new DateTimeOffset(2026, 9, 3, 10, 0, 0, TimeSpan.Zero)),
            derivations,
            policies);

    private static PolicyDefinition Policy(
        string name,
        PolicyEffect effect,
        int priority,
        string conditions,
        string pattern = "app:finance/vendor:create") =>
        new()
        {
            Name = name,
            Effect = effect,
            Priority = priority,
            PermissionPattern = pattern,
            ConditionsJson = conditions
        };

    private static AccessDerivation Derivation() =>
        new(Guid.NewGuid(), "app:finance/vendor:create", ["role:finance"]);

    private static UserIdentity User() => new()
    {
        Id = Guid.NewGuid(),
        Department = "Finance",
        CostCentre = "CC-FIN",
        EmploymentType = EmploymentType.Employee,
        Clearance = 3,
        Status = IdentityStatus.Active
    };
}
