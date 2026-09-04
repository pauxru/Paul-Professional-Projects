using System.Security.Cryptography;
using Auth.Bridge;
using Auth.Legacy;
using Auth.Modern;
using Auth.Passwords;

namespace Auth.Tests;

/// <summary>
/// Three authentication stacks, one canonical principal.
///
/// The functional requirement is that all three agree. The security consequence is that
/// an account is only as strong as the weakest stack still enabled for it -- which is why
/// the same fixture proves both, in the same file, a few tests apart.
/// </summary>
public sealed class SessionBridgeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private const string Issuer = "urn:adfs.example.test";
    private const string OidcIssuer = "https://login.example.test";
    private const string Audience = "urn:ledger";
    private const string ClientId = "ledger-web";

    private static readonly byte[] EncryptionKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] ValidationKey = Enumerable.Range(64, 64).Select(i => (byte)i).ToArray();

    private readonly RSA _samlKey = RSA.Create(2048);
    private readonly RSA _jwtKey = RSA.Create(2048);

    public void Dispose()
    {
        _samlKey.Dispose();
        _jwtKey.Dispose();
    }

    private UserDirectory Directory(string hash) =>
        new UserDirectory().Tap(d => d.Add(new UserRecord
        {
            Subject = "finance.director",
            Tenant = "north",
            Roles = new HashSet<string> { "Admin", "Manager" },
            PasswordHash = hash,
        }));

    private SessionBridge Bridge(
        UserDirectory directory,
        StackConfiguration? configuration = null,
        IClaimsTransformer? transformer = null,
        MigratingPasswordHasher? hasher = null) =>
        new(directory,
            new LegacyTicketProtector(EncryptionKey, ValidationKey),
            new WsFederation(_samlKey, Issuer),
            new JwtCodec(_jwtKey),
            transformer ?? new CorrectedClaimsTransformer(),
            hasher ?? new MigratingPasswordHasher(HashPolicy.Fast),
            configuration ?? StackConfiguration.Coexistence);

    private string FormsTicketFor(string subject, string roles = "Admin|Manager") =>
        new LegacyTicketProtector(EncryptionKey, ValidationKey).Protect(
            new FormsTicket(2, subject, Now, Now.AddMinutes(30), false, roles, "/"));

    private string SamlFor(string subject) =>
        new WsFederation(_samlKey, Issuer).Issue(new SamlAssertion(
            $"_{Guid.NewGuid():N}", Issuer, subject, Now.AddMinutes(-1), Now.AddMinutes(30), Audience,
            [new KeyValuePair<string, string>(ClaimNames.LegacyRole, "Admin"),
             new KeyValuePair<string, string>(ClaimNames.LegacyRole, "Manager")]));

    private string IdTokenFor(string subject)
    {
        var authority = new OidcAuthority(new JwtCodec(_jwtKey), OidcIssuer,
            [new OidcClient(ClientId, ["https://ledger.example.test/cb"])]);

        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            ClientId, "https://ledger.example.test/cb", "n", OidcAuthority.Challenge(verifier),
            "S256", subject,
            [new KeyValuePair<string, string>("tenant", "north"),
             new KeyValuePair<string, string>("roles", "Admin|Manager")],
            Now);

        return authority.Redeem(code!, ClientId, "https://ledger.example.test/cb", verifier, Now)
                        .Tokens!.IdToken;
    }

    private MigratingPasswordHasher Hasher => new(HashPolicy.Fast);

    // ------------------------------------------------------------------ all three agree

    [Fact]
    public void AllThreeStacksProduceTheSameCanonicalPrincipal()
    {
        var directory = Directory(Hasher.HashNew("hunter2"));
        var bridge = Bridge(directory);

        var forms = bridge.FromFormsTicket(FormsTicketFor("finance.director"), Now);
        var saml = bridge.FromWsFederation(SamlFor("finance.director"), Audience, Now);
        var oidc = bridge.FromIdToken(IdTokenFor("finance.director"), OidcIssuer, ClientId, Now);

        Assert.True(forms.Ok);
        Assert.True(saml.Ok);
        Assert.True(oidc.Ok);

        foreach (var resource in Resources.All)
        {
            Assert.Equal(forms.Principal!.Has(resource), saml.Principal!.Has(resource));
            Assert.Equal(forms.Principal.Has(resource), oidc.Principal!.Has(resource));
        }

        Assert.Equal("finance.director", forms.Principal!.Subject);
        Assert.Equal("north", forms.Principal.Tenant);
    }

    [Fact]
    public void EachStackRecordsWhereThePrincipalCameFrom()
    {
        // The principal is identical; the provenance is not. An audit that cannot say which
        // stack authenticated a session cannot answer "was anyone still using the old path
        // on the day we turned it off", which is the only question that matters at cutover.
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")));

        Assert.Equal(AuthenticationSource.FormsTicket,
                     bridge.FromFormsTicket(FormsTicketFor("finance.director"), Now).Source);
        Assert.Equal(AuthenticationSource.WsFederation,
                     bridge.FromWsFederation(SamlFor("finance.director"), Audience, Now).Source);
        Assert.Equal(AuthenticationSource.Oidc,
                     bridge.FromIdToken(IdTokenFor("finance.director"), OidcIssuer, ClientId, Now).Source);
    }

    // ------------------------------------------------------------------ stack gating

    [Fact]
    public void DisabledStacksRefuseValidCredentials()
    {
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")), StackConfiguration.After);

        var forms = bridge.FromFormsTicket(FormsTicketFor("finance.director"), Now);
        Assert.False(forms.Ok);
        Assert.Equal(BridgeFailure.StackDisabled, forms.Failure);

        Assert.Equal(BridgeFailure.StackDisabled,
                     bridge.FromWsFederation(SamlFor("finance.director"), Audience, Now).Failure);
        Assert.True(bridge.FromIdToken(IdTokenFor("finance.director"), OidcIssuer, ClientId, Now).Ok);
    }

    [Fact]
    public void BeforeCutoverTheModernStackIsOff()
    {
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")), StackConfiguration.Before);

        Assert.True(bridge.FromFormsTicket(FormsTicketFor("finance.director"), Now).Ok);
        Assert.Equal(BridgeFailure.StackDisabled,
                     bridge.FromIdToken(IdTokenFor("finance.director"), OidcIssuer, ClientId, Now).Failure);
    }

    [Fact]
    public void StackCountsMatchThePhase()
    {
        // Three authentication paths; the fourth flag is rehash-on-login, which is a
        // behaviour rather than a door.
        Assert.Equal(2, StackConfiguration.Before.EnabledStacks);
        Assert.Equal(3, StackConfiguration.Coexistence.EnabledStacks);
        Assert.Equal(1, StackConfiguration.After.EnabledStacks);

        Assert.False(StackConfiguration.Before.RehashOnLogin);
        Assert.True(StackConfiguration.Coexistence.RehashOnLogin);
    }

    // ------------------------------------------------------------------ the finding

    [Fact]
    public void AFullyMigratedAccountIsStillReachableThroughTheLegacyTicket()
    {
        // The finding, stated as an executable claim. The account's password is a long
        // passphrase hashed with Argon2id; it signs in through OIDC; and it is one forged
        // Forms ticket away from compromise for as long as that stack stays enabled.
        //
        // The Argon2id hash is not merely insufficient here -- it is *irrelevant*. The
        // attack never touches it. Account security is the minimum over every enabled path,
        // and the credential path is the only one anybody measures.
        var directory = Directory(Hasher.HashNew("correct horse battery staple x9"));
        var forged = FormsTicketFor("finance.director", "Admin|Manager");

        var during = Bridge(directory, StackConfiguration.Coexistence);
        var after = Bridge(directory, StackConfiguration.After);

        Assert.True(during.FromFormsTicket(forged, Now).Ok);
        Assert.False(after.FromFormsTicket(forged, Now).Ok);
    }

    [Fact]
    public void ThePasswordStrengthMakesNoDifferenceToTheTicketPath()
    {
        var weak = Bridge(Directory(Hasher.HashNew("password1")));
        var strong = Bridge(Directory(Hasher.HashNew("correct horse battery staple x9")));
        var ticket = FormsTicketFor("finance.director");

        Assert.True(weak.FromFormsTicket(ticket, Now).Ok);
        Assert.True(strong.FromFormsTicket(ticket, Now).Ok);
    }

    [Fact]
    public void ARolePromotionInAForgedTicketIsHonoured()
    {
        // The ticket carries its own roles. Trusting them is what makes a leaked validation
        // key a privilege escalation rather than merely an impersonation.
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")));
        var result = bridge.FromFormsTicket(FormsTicketFor("finance.director", "Admin|Auditor"), Now);

        Assert.True(result.Ok);
        Assert.NotEqual(
            bridge.FromFormsTicket(FormsTicketFor("finance.director", "Clerk"), Now).Principal!.Scopes.Count,
            result.Principal!.Scopes.Count);
    }

    // ------------------------------------------------------------------ sign-in and rehash

    [Fact]
    public void SignInUpgradesALegacyHashAndCommitsIt()
    {
        var legacy = HashCodec.Format(PasswordAlgorithms.HashMembershipSha1(
            "hunter2", Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()));

        var directory = Directory(legacy);
        var bridge = Bridge(directory);

        var (result, verification) = bridge.SignIn(
            "finance.director", "hunter2", AuthenticationSource.Oidc, Now);

        Assert.True(result.Ok);
        Assert.Equal(VerifyOutcome.SucceededNeedsRehash, verification.Outcome);

        bridge.CommitRehash("finance.director", verification.UpgradedHash!, Now);

        var user = directory.Find("finance.director")!;
        Assert.Equal(HashFormat.Argon2id, HashCodec.Parse(user.PasswordHash).Format);
        Assert.Equal(Now, user.RehashedAt);
    }

    [Fact]
    public void RehashedAtIsSetOnceAndOnlyOnce()
    {
        // The migration is measured by counting these. A field that resets on every login
        // makes the count meaningless and the ceiling invisible.
        var directory = Directory(Hasher.HashNew("hunter2"));
        var bridge = Bridge(directory);

        bridge.CommitRehash("finance.director", Hasher.HashNew("hunter2"), Now);
        bridge.CommitRehash("finance.director", Hasher.HashNew("hunter2"), Now.AddDays(1));

        Assert.Equal(Now, directory.Find("finance.director")!.RehashedAt);
    }

    [Fact]
    public void CommitRehashOnAnUnknownUserIsANoOp()
    {
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")));
        bridge.CommitRehash("nobody", Hasher.HashNew("x"), Now);
    }

    [Fact]
    public void SignInWithTheWrongPasswordFails()
    {
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")));
        var (result, verification) = bridge.SignIn(
            "finance.director", "wrong", AuthenticationSource.Oidc, Now);

        Assert.False(result.Ok);
        Assert.False(verification.Succeeded);
        Assert.Null(verification.UpgradedHash);
    }

    [Fact]
    public void SignInForAnUnknownUserStillDoesTheWork()
    {
        // The response must not answer "does this account exist" through the clock. The
        // decoy verification is what makes the two paths cost the same.
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")));
        var (result, verification) = bridge.SignIn(
            "nobody.here", "hunter2", AuthenticationSource.Oidc, Now);

        Assert.Equal(BridgeFailure.UnknownUser, result.Failure);
        Assert.False(verification.Succeeded);
        Assert.True(verification.ElapsedMilliseconds > 0);
    }

    [Fact]
    public void OnlyTheModernStackChecksThatTheUserStillExists()
    {
        // An asymmetry, pinned rather than tidied away, because it is the sharpest single
        // difference between the stacks.
        //
        // The legacy paths take their roles from the credential, exactly as the original
        // application did, and never consult the directory. So a ticket or an assertion for
        // a subject the directory has never heard of -- a terminated employee, a deleted
        // contractor, a name the attacker invented -- authenticates successfully. The OIDC
        // path looks the subject up and refuses.
        //
        // Deprovisioning therefore does not take effect on the legacy paths at all. That is
        // faithful to the system being modernised and it is a hole; making the bridge check
        // the directory on every path is a one-line change, and it is a *behaviour* change
        // that will lock out anyone whose directory row is missing for an unrelated reason.
        // The decision belongs to whoever owns the outage, so it is recorded here rather
        // than made silently. See docs/known-limitations.md.
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")));

        Assert.True(bridge.FromFormsTicket(FormsTicketFor("ghost"), Now).Ok);
        Assert.True(bridge.FromWsFederation(SamlFor("ghost"), Audience, Now).Ok);
        Assert.Equal(BridgeFailure.UnknownUser,
                     bridge.FromIdToken(IdTokenFor("ghost"), OidcIssuer, ClientId, Now).Failure);
    }

    [Fact]
    public void AnUnknownSubjectGetsNoTenant()
    {
        // The consolation prize: the principal exists but carries no tenant, so any
        // downstream check that is tenant-scoped fails closed. It is not a substitute for
        // the directory check -- it just narrows what the forged principal can reach.
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")));
        var result = bridge.FromFormsTicket(FormsTicketFor("ghost"), Now);

        Assert.Equal("unknown", result.Principal!.Tenant);
    }

    [Fact]
    public void RejectedCredentialsReportTheStackNotTheReason()
    {
        // The bridge tells its caller that the stack refused; it does not forward the
        // stack's specific complaint. Which byte of the ticket was wrong is an operator's
        // question, and it belongs in the log rather than in the response.
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")));

        Assert.Equal(BridgeFailure.RejectedByStack,
                     bridge.FromFormsTicket("deadbeef", Now).Failure);
        Assert.Equal(BridgeFailure.RejectedByStack,
                     bridge.FromWsFederation("<nope/>", Audience, Now).Failure);
        Assert.Equal(BridgeFailure.RejectedByStack,
                     bridge.FromIdToken("a.b.c", OidcIssuer, ClientId, Now).Failure);
    }

    [Fact]
    public void ExpiredCredentialsAreRejectedOnEveryStack()
    {
        var bridge = Bridge(Directory(Hasher.HashNew("hunter2")));
        var later = Now.AddDays(1);

        Assert.False(bridge.FromFormsTicket(FormsTicketFor("finance.director"), later).Ok);
        Assert.False(bridge.FromWsFederation(SamlFor("finance.director"), Audience, later).Ok);
        Assert.False(bridge.FromIdToken(IdTokenFor("finance.director"), OidcIssuer, ClientId, later).Ok);
    }

    // ------------------------------------------------------------------ directory

    [Fact]
    public void TheDirectoryCountsByFormat()
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var directory = new UserDirectory();

        directory.Add(new UserRecord
        {
            Subject = "a", Tenant = "north", Roles = new HashSet<string>(),
            PasswordHash = HashCodec.Format(PasswordAlgorithms.HashMembershipSha1("p", salt)),
        });
        directory.Add(new UserRecord
        {
            Subject = "b", Tenant = "north", Roles = new HashSet<string>(),
            PasswordHash = HashCodec.Format(PasswordAlgorithms.HashArgon2id("p", salt, 8, 1, 1)),
        });

        Assert.Equal(1, directory.CountByFormat(HashFormat.MembershipSha1));
        Assert.Equal(1, directory.CountByFormat(HashFormat.Argon2id));
        Assert.Equal(0, directory.CountByFormat(HashFormat.IdentityV2));
        Assert.Equal(2, directory.All.Count);
    }

    [Fact]
    public void AnUnknownSubjectIsNull()
    {
        Assert.Null(new UserDirectory().Find("nobody"));
    }
}

internal static class DirectoryExtensions
{
    public static UserDirectory Tap(this UserDirectory directory, Action<UserDirectory> action)
    {
        action(directory);
        return directory;
    }
}
