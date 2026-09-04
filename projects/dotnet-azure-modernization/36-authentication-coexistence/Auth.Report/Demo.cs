using System.Globalization;
using System.Security.Cryptography;
using Auth.Bridge;
using Auth.Legacy;
using Auth.Modern;
using Auth.Passwords;

namespace Auth.Report;

/// <summary>
/// A single user's journey through the whole migration, printed as it happens.
/// </summary>
/// <remarks>
/// The report proves the findings over populations and decision spaces. This shows one
/// account, one day at a time, because the population-level claims are easier to believe
/// once you have watched the individual mechanics that produce them.
/// </remarks>
public static class Demo
{
    public static int Run()
    {
        var now = new DateTimeOffset(2026, 1, 12, 8, 30, 0, TimeSpan.Zero);

        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        var validationKey = RandomNumberGenerator.GetBytes(32);
        var hardened = new HardenedTicketProtector(encryptionKey, validationKey);
        var legacyProtector = new LegacyTicketProtector(encryptionKey, validationKey);

        using var rsa = RSA.Create(2048);
        var federation = new WsFederation(rsa, "urn:legacy-sts");
        var jwt = new JwtCodec(rsa);

        // Fast parameters: this is a demonstration, not a benchmark. The report uses the
        // deployable RFC 9106 profile.
        var policy = HashPolicy.Fast;
        var hasher = new MigratingPasswordHasher(policy);

        var directory = new UserDirectory();
        var roles = new HashSet<string>(
            [LegacyRoles.Admin, LegacyRoles.Auditor], StringComparer.Ordinal);

        // A user still sitting on the 2005 scheme, because nobody has touched this account
        // since the first migration stalled.
        var salt = RandomNumberGenerator.GetBytes(16);
        var legacyHash = HashCodec.Format(
            PasswordAlgorithms.HashMembershipSha1("Winter2019!", salt));

        directory.Add(new UserRecord
        {
            Subject = "r.okonkwo",
            Tenant = "hq",
            Roles = roles,
            PasswordHash = legacyHash,
        });

        var bridge = new SessionBridge(
            directory, hardened, federation, jwt,
            new NaiveClaimsTransformer(), hasher, StackConfiguration.Before);

        Header("Day 0 -- before anything changes");
        var user = directory.Find("r.okonkwo")!;
        Console.WriteLine($"  {user.Subject} holds a {HashCodec.Parse(user.PasswordHash).Format} hash.");
        Console.WriteLine($"  Attacker cost per guess: "
            + $"{HashCodec.Parse(user.PasswordHash).AttackerCompressionsPerGuess:N0} compressions.");
        Console.WriteLine($"  Roles: {string.Join(", ", roles.OrderBy(r => r, StringComparer.Ordinal))}");
        Console.WriteLine($"  Stacks enabled: {bridge.Configuration.EnabledStacks}"
            + " (Forms tickets, WS-Federation)");

        Header("Day 1 -- rehash-on-login ships");
        bridge.Configuration = StackConfiguration.Coexistence;
        var (result, verification) = bridge.SignIn(
            "r.okonkwo", "Winter2019!", AuthenticationSource.FormsTicket, now);

        Console.WriteLine($"  Sign-in succeeded: {result.Ok}");
        Console.WriteLine($"  Verified against: {verification.StoredFormat}");
        Console.WriteLine($"  Outcome: {verification.Outcome}");

        if (result.UpgradedHash is not null)
        {
            bridge.CommitRehash("r.okonkwo", result.UpgradedHash, now);
            var upgraded = HashCodec.Parse(directory.Find("r.okonkwo")!.PasswordHash);
            Console.WriteLine($"  Stored hash is now: {upgraded.Format} "
                + $"(m={upgraded.MemoryKib} KiB, t={upgraded.Iterations}, p={upgraded.Parallelism})");
            Console.WriteLine($"  Attacker cost per guess: "
                + $"{upgraded.AttackerCompressionsPerGuess:N0} compressions "
                + $"({upgraded.AttackerCompressionsPerGuess / 1:N0}x, and that ignores the memory term entirely).");
            Console.WriteLine("  No password reset email was sent. The user noticed nothing.");
        }

        Header("Day 1, one minute later -- what the upgrade did not buy");
        var forged = legacyProtector.Protect(new FormsTicket(
            2, "r.okonkwo", now, now.AddHours(8), true, string.Join('|', roles), "/"));
        Console.WriteLine("  Suppose the legacy validation key from web.config has leaked.");

        var leakyBridge = new SessionBridge(
            directory, legacyProtector, federation, jwt,
            new NaiveClaimsTransformer(), hasher, StackConfiguration.Coexistence);
        var forgedResult = leakyBridge.FromFormsTicket(forged, now);
        Console.WriteLine($"  Forged ticket accepted during coexistence: {forgedResult.Ok}");

        leakyBridge.Configuration = StackConfiguration.After;
        Console.WriteLine($"  Forged ticket accepted after the cutoff:  "
            + $"{leakyBridge.FromFormsTicket(forged, now).Ok}");
        Console.WriteLine("  The Argon2id hash is irrelevant to this attack. Only the date is.");

        Header("Day 1 -- what the claims transformation did to this user");
        var naive = new NaiveClaimsTransformer();
        var denyAware = new DenyAwareClaimsTransformer();

        var (naiveScopes, naiveDenies) = naive.Transform(roles);
        var (safeScopes, safeDenies) = denyAware.Transform(roles);

        var context = new RequestContext(IsLocalNetwork: true, TenantMatches: true, IsTemporaryStaff: false);

        Console.WriteLine($"  Legacy roles: {string.Join(", ", roles.OrderBy(r => r, StringComparer.Ordinal))}");
        Console.WriteLine();
        Console.WriteLine($"  {"resource",-24} {"legacy",-8} {"naive",-8} {"deny-aware",-10}");
        foreach (var resource in Resources.All)
        {
            var legacyDecision = LegacyAuthorization.Allows(roles, resource, context);
            var naiveDecision = ModernAuthorization.Allows(
                Principal(naiveScopes, naiveDenies), resource, context);
            var safeDecision = ModernAuthorization.Allows(
                Principal(safeScopes, safeDenies), resource, context);

            var flag = legacyDecision != naiveDecision ? "  <-- escalation" : "";
            Console.WriteLine($"  {resource,-24} {legacyDecision,-8} {naiveDecision,-8} {safeDecision,-10}{flag}");
        }

        Console.WriteLine();
        Console.WriteLine("  This user is an Admin who is also an Auditor. The legacy code says");
        Console.WriteLine("  \"(Admin || Clerk) && !Auditor\" for ledger writes. A role-to-scope table");
        Console.WriteLine("  has nowhere to put the second half of that sentence.");

        Header("Day 1 -- the same user, through all three front doors");
        var samlXml = federation.Issue(new SamlAssertion(
            "_demo1", "urn:legacy-sts", "r.okonkwo", now.AddMinutes(-1), now.AddMinutes(30),
            "ledger-app",
            roles.Select(r => new KeyValuePair<string, string>(ClaimNames.LegacyRole, r)).ToList()));

        var authority = new OidcAuthority(jwt, "https://login.example",
            [new OidcClient("ledger-app", ["https://ledger.example/cb"])]);
        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            "ledger-app", "https://ledger.example/cb", "nonce-1",
            OidcAuthority.Challenge(verifier), "S256", "r.okonkwo", [], now);
        var tokens = authority.Redeem(code!, "ledger-app", "https://ledger.example/cb", verifier, now);

        var viaForms = bridge.FromFormsTicket(
            hardened.Protect(new FormsTicket(
                2, "r.okonkwo", now, now.AddHours(1), false, string.Join('|', roles), "/")), now);
        var viaSaml = bridge.FromWsFederation(samlXml, "ledger-app", now);
        var viaOidc = bridge.FromIdToken(
            tokens.Tokens!.IdToken, "https://login.example", "ledger-app", now, "nonce-1");

        foreach (var (label, r) in new[]
                 {
                     ("Forms ticket", viaForms), ("WS-Federation", viaSaml), ("OIDC id_token", viaOidc),
                 })
        {
            Console.WriteLine($"  {label,-16} -> subject={r.Principal?.Subject} "
                + $"scopes={r.Principal?.Scopes.Count} source={r.Principal?.Source}");
        }
        Console.WriteLine();
        Console.WriteLine("  Application code downstream cannot tell which door was used. That is the");
        Console.WriteLine("  point -- and it is why switching a door off is a config change, not a rewrite.");

        Header("Day 400 -- where the migration actually is");
        var run = MigrationModel.Run(Population.Typical, 20000, 1095, 20260904);
        foreach (var day in new[] { 7, 30, 90, 365, 730, 1095 })
        {
            Console.WriteLine($"  day {day,5}: {(run.FractionMigratedAt(day) * 100).ToString("F2", CultureInfo.InvariantCulture),6}% migrated");
        }
        Console.WriteLine($"  ceiling:   "
            + $"{(MigrationModel.AsymptoticFractionMigrated(Population.Typical) * 100).ToString("F2", CultureInfo.InvariantCulture),6}% "
            + "-- the rest never sign in again");
        Console.WriteLine();
        Console.WriteLine("  Rehash-on-login migrates everyone who comes back. The plan for everyone");
        Console.WriteLine("  who does not has to exist on day one, because it will still be needed");
        Console.WriteLine("  in three years.");

        Console.WriteLine();
        Console.WriteLine("Full measurements: docs/results.md");
        return 0;

        CanonicalPrincipal Principal(IReadOnlySet<string> scopes, IReadOnlySet<string> denies) =>
            new("r.okonkwo", AuthenticationSource.Oidc, scopes, denies, "hq", now);
    }

    private static void Header(string title)
    {
        Console.WriteLine();
        Console.WriteLine(title);
        Console.WriteLine(new string('-', title.Length));
    }
}
