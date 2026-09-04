using Auth.Bridge;

namespace Auth.Tests;

/// <summary>
/// The claims transformation, and the reason it cannot be finished.
///
/// The counts below are pinned as literals. That is deliberate: they are the project's
/// headline numbers, they appear in the report and in the README, and a change to the
/// legacy policy that quietly moves them should break the build rather than quietly
/// republish a different claim.
/// </summary>
public sealed class AuthorizationTests
{
    private static IReadOnlySet<string> Roles(params string[] roles) => roles.ToHashSet();

    private static readonly RequestContext Plain = new(false, false, false);

    /// <summary>The context the report uses: on the corporate network, tenant matches, not temp staff.</summary>
    private static readonly RequestContext Reported = new(true, true, false);

    [Fact]
    public void TheDecisionSpaceIsWhatItClaimsToBe()
    {
        Assert.Equal(4096, DivergenceAnalysis.TotalDecisions);
        Assert.Equal(64, LegacyAuthorization.AllRoleSets().Count());
        Assert.Equal(8, LegacyAuthorization.AllContexts().Count());
        Assert.Equal(8, Resources.All.Length);
        Assert.Equal(6, LegacyRoles.All.Length);
    }

    [Fact]
    public void RoleSetsAreDistinctAndCoverThePowerSet()
    {
        var sets = LegacyAuthorization.AllRoleSets().ToList();
        var signatures = sets.Select(s => string.Join('+', s.OrderBy(r => r, StringComparer.Ordinal)))
                             .ToHashSet();

        Assert.Equal(64, signatures.Count);
        Assert.Contains("", signatures);
        Assert.Contains("Admin+Auditor+Clerk+Manager+Temp+Vendor", signatures);
    }

    [Fact]
    public void LegacyAuthorizationIsNotMonotone()
    {
        // The finding the whole project rests on. A role-to-scope table unions scopes, so it
        // is monotone by construction: adding a role can only add permissions. The legacy
        // policy is not, because it contains clauses of the form `IsInRole(a) && !IsInRole(b)`.
        // A monotone function cannot agree everywhere with a non-monotone one, so the naive
        // transformation is not incomplete -- it is impossible.
        var counterexamples = LegacyAuthorization.MonotonicityCounterexamples(Reported).ToList();
        Assert.Equal(72, counterexamples.Count);
    }

    [Fact]
    public void TheCanonicalCounterexampleIsWhatTheReportSays()
    {
        // Admin can write the ledger. Admin *and* Auditor cannot, because the separation of
        // duties rule is expressed as a negation. This single pair is the entire argument.
        Assert.True(LegacyAuthorization.Allows(Roles("Admin"), Resources.WriteLedger, Reported));
        Assert.False(LegacyAuthorization.Allows(Roles("Admin", "Auditor"), Resources.WriteLedger, Reported));
    }

    [Fact]
    public void EveryCounterexampleIsRealAndInTheRightDirection()
    {
        foreach (var (smaller, larger, resource) in LegacyAuthorization.MonotonicityCounterexamples(Reported))
        {
            Assert.True(smaller.IsProperSubsetOf(larger));
            Assert.Single(larger.Except(smaller));

            var lost = LegacyAuthorization.AllContexts().Any(context =>
                LegacyAuthorization.Allows(smaller, resource, context) &&
                !LegacyAuthorization.Allows(larger, resource, context));

            Assert.True(lost, $"{string.Join('+', smaller)} -> {string.Join('+', larger)} on {resource}");
        }
    }

    [Fact]
    public void TheNaiveTransformationDivergesOn592Decisions()
    {
        var divergences = DivergenceAnalysis.Compare(new NaiveClaimsTransformer());
        Assert.Equal(592, divergences.Count);
    }

    [Fact]
    public void EveryNaiveDivergenceIsAnEscalation()
    {
        // The direction is the finding, not the count. A migration whose failures are all
        // lockouts generates support tickets on day one and gets fixed. A migration whose
        // failures are all escalations passes user acceptance testing, because nobody has
        // ever filed a ticket about a door that should have been locked.
        var divergences = DivergenceAnalysis.Compare(new NaiveClaimsTransformer());

        Assert.Equal(592, divergences.Count(d => d.Kind == DivergenceKind.Escalation));
        Assert.Equal(0, divergences.Count(d => d.Kind == DivergenceKind.Lockout));
    }

    [Fact]
    public void TheDenyChannelReducesDivergenceToSixteen()
    {
        // A change of shape, not a longer table.
        Assert.Equal(16, DivergenceAnalysis.Compare(new DenyAwareClaimsTransformer()).Count);
    }

    [Fact]
    public void CorrectingOneGrantRowRemovesTheRemainder()
    {
        // The 16 survivors were never a limit of the deny channel: `Temp` had been granted
        // `ledger.read`, which the legacy policy never gave it. Of the original 592, 576
        // were structural and 16 were an ordinary data error -- and both produce identical
        // symptoms, which is why "review the mapping more carefully" fixes only one of them.
        Assert.Empty(DivergenceAnalysis.Compare(new CorrectedClaimsTransformer()));
    }

    [Fact]
    public void TheSurvivingSixteenAreAllTheSameMistake()
    {
        var divergences = DivergenceAnalysis.Compare(new DenyAwareClaimsTransformer());

        Assert.All(divergences, d => Assert.Equal(Resources.ReadLedger, d.Resource));
        Assert.All(divergences, d => Assert.Contains(LegacyRoles.Temp, d.Roles));
        Assert.All(divergences, d => Assert.Equal(DivergenceKind.Escalation, d.Kind));

        // Two role sets ({Temp} and {Temp,Vendor}) across all eight contexts.
        Assert.Equal(2, divergences.Select(d => d.RoleList).Distinct().Count());
        Assert.Equal(8, divergences.Count(d => d.RoleList == "Temp"));
    }

    [Fact]
    public void AMonotoneTableCannotReproduceANonMonotonePolicy()
    {
        // Stated as a test rather than as a comment: for each counterexample, verify that
        // the naive transformer really does grant the larger role set at least as much as
        // the smaller one. That is monotonicity, and it is the property that makes the
        // divergences unavoidable.
        var transformer = new NaiveClaimsTransformer();

        foreach (var (smaller, larger, _) in LegacyAuthorization.MonotonicityCounterexamples(Reported))
        {
            var (smallScopes, _) = transformer.Transform(smaller);
            var (largeScopes, _) = transformer.Transform(larger);
            Assert.True(smallScopes.IsSubsetOf(largeScopes));
        }
    }

    [Fact]
    public void TheDenyAwareTransformerIsNotMonotone()
    {
        // And this is why it can succeed where the naive one cannot: the deny channel gives
        // an added role the ability to remove a permission, which is exactly the shape of
        // the legacy policy.
        var transformer = new DenyAwareClaimsTransformer();

        static CanonicalPrincipal Principal((IReadOnlySet<string>, IReadOnlySet<string>) t) =>
            new("u", AuthenticationSource.Oidc, t.Item1, t.Item2, "north", DateTimeOffset.UnixEpoch);

        var nonMonotone = LegacyAuthorization.MonotonicityCounterexamples(Reported).Any(c =>
        {
            var small = Principal(transformer.Transform(c.Smaller));
            var large = Principal(transformer.Transform(c.Larger));
            return Resources.All.Any(r => small.Has(r) && !large.Has(r));
        });

        Assert.True(nonMonotone);
    }

    [Fact]
    public void DenyBeatsGrant()
    {
        var principal = new CanonicalPrincipal(
            "u", AuthenticationSource.Oidc,
            new HashSet<string> { "ledger.write" }, new HashSet<string> { "ledger.write" },
            "north", DateTimeOffset.UnixEpoch);

        Assert.False(principal.Has("ledger.write"));
    }

    [Fact]
    public void AScopeWithNoDenyIsGranted()
    {
        var principal = new CanonicalPrincipal(
            "u", AuthenticationSource.Oidc,
            new HashSet<string> { "ledger.write" }, new HashSet<string>(),
            "north", DateTimeOffset.UnixEpoch);

        Assert.True(principal.Has("ledger.write"));
        Assert.False(principal.Has("ledger.read"));
    }

    [Fact]
    public void AnEmptyRoleSetGrantsNothing()
    {
        foreach (var resource in Resources.All)
        {
            foreach (var context in LegacyAuthorization.AllContexts())
            {
                Assert.False(LegacyAuthorization.Allows(Roles(), resource, context));
            }
        }
    }

    [Fact]
    public void ContextMattersForAtLeastOneResource()
    {
        // If no decision depended on the request context, the context dimension would be
        // 4096/8 = 512 identical copies and the whole exhaustive sweep would be theatre.
        var contextSensitive = Resources.All.Any(resource =>
            LegacyAuthorization.AllRoleSets().Any(roles =>
                LegacyAuthorization.AllContexts()
                                   .Select(c => LegacyAuthorization.Allows(roles, resource, c))
                                   .Distinct().Count() > 1));

        Assert.True(contextSensitive);
    }

    [Fact]
    public void TheTransformersAreNamed()
    {
        Assert.Equal("naive", new NaiveClaimsTransformer().Name);
        Assert.Equal("deny-aware", new DenyAwareClaimsTransformer().Name);
        Assert.Equal("deny-aware, table corrected", new CorrectedClaimsTransformer().Name);
    }

    [Fact]
    public void TransformationIsDeterministic()
    {
        var transformer = new DenyAwareClaimsTransformer();
        foreach (var roles in LegacyAuthorization.AllRoleSets())
        {
            var (a, b) = transformer.Transform(roles);
            var (c, d) = transformer.Transform(roles);
            Assert.True(a.SetEquals(c));
            Assert.True(b.SetEquals(d));
        }
    }

    [Fact]
    public void TransformationIgnoresRoleOrder()
    {
        var transformer = new DenyAwareClaimsTransformer();
        var (a, _) = transformer.Transform(Roles("Admin", "Auditor"));
        var (b, _) = transformer.Transform(Roles("Auditor", "Admin"));
        Assert.True(a.SetEquals(b));
    }

    [Fact]
    public void UnknownRolesAreIgnoredRatherThanFatal()
    {
        // Real directories contain roles nobody remembers creating. Throwing on one turns a
        // stale group membership into a failed login.
        var (scopes, _) = new DenyAwareClaimsTransformer().Transform(Roles("Admin", "AcmeImport2011"));
        Assert.NotEmpty(scopes);
    }

    [Fact]
    public void EveryDivergenceRecordIsSelfDescribing()
    {
        // An operator triaging one of these has the role set, the resource and the context
        // in front of them. A count on its own is not actionable.
        foreach (var divergence in DivergenceAnalysis.Compare(new NaiveClaimsTransformer()))
        {
            Assert.Contains(divergence.Resource, Resources.All);
            Assert.All(divergence.Roles, r => Assert.Contains(r, LegacyRoles.All));
            Assert.Contains(divergence.Kind, new[] { DivergenceKind.Escalation, DivergenceKind.Lockout });
        }
    }
}
