using System.Diagnostics;
using System.Security.Cryptography;
using Auth.Bridge;
using Auth.Legacy;
using Auth.Modern;
using Auth.Passwords;

namespace Auth.Report;

public sealed record TimingSample(HashFormat Format, double Milliseconds);

public sealed record TimingChannelResult(
    bool ConstantWork,
    IReadOnlyDictionary<HashFormat, double> MedianMilliseconds,
    double FourWayAccuracy,
    double UnmigratedDetectionAccuracy,
    double MedianOverall);

public sealed record DivergenceSummary(
    string Transformer,
    int Total,
    int Escalations,
    int Lockouts,
    IReadOnlyList<Divergence> Examples);

/// <summary>
/// Every measurement in the report. Each method answers exactly one question and returns
/// data rather than prose, so the same numbers drive the tests and the document.
/// </summary>
public static class Experiments
{
    /// <summary>
    /// Enumerates the full decision space for a transformation. 4096 decisions, no sampling.
    /// </summary>
    public static DivergenceSummary Divergences(IClaimsTransformer transformer)
    {
        var all = DivergenceAnalysis.Compare(transformer);
        return new DivergenceSummary(
            transformer.Name,
            all.Count,
            all.Count(d => d.Kind == DivergenceKind.Escalation),
            all.Count(d => d.Kind == DivergenceKind.Lockout),
            all.Take(6).ToList());
    }

    /// <summary>
    /// Searches for role sets where adding a role removes a permission. One counterexample
    /// is enough to settle the question; the count says how pervasive the pattern is.
    /// </summary>
    public static IReadOnlyList<(string Smaller, string Larger, string Resource)> Monotonicity()
    {
        var context = new RequestContext(IsLocalNetwork: true, TenantMatches: true, IsTemporaryStaff: false);
        return LegacyAuthorization.MonotonicityCounterexamples(context)
            .Select(c => (
                Smaller: c.Smaller.Count == 0 ? "(none)" : string.Join('+', c.Smaller.OrderBy(r => r, StringComparer.Ordinal)),
                Larger: string.Join('+', c.Larger.OrderBy(r => r, StringComparer.Ordinal)),
                c.Resource))
            .ToList();
    }

    /// <summary>
    /// Measures how much an unauthenticated caller learns from the clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The attack this quantifies is not password recovery. It is reconnaissance: submitting
    /// a deliberately wrong password to the ordinary login endpoint and reading the response
    /// time to learn which accounts are still stored under a weak hash. That turns an
    /// eventual database breach from "crack what you can" into a target list prepared in
    /// advance, and it is available to anyone who can reach the login page.
    /// </para>
    /// <para>
    /// The classifier is deliberately the crudest thing that could work -- nearest median on
    /// a single sample -- because the finding is about how much separation exists, not about
    /// how clever an attacker would need to be. A real one would average over repeats and do
    /// considerably better.
    /// </para>
    /// </remarks>
    public static TimingChannelResult TimingChannel(bool constantWork, int samplesPerFormat, HashPolicy policy)
    {
        var hasher = new MigratingPasswordHasher(policy, constantWork);
        var salt = RandomNumberGenerator.GetBytes(16);

        var stored = new Dictionary<HashFormat, string>
        {
            [HashFormat.MembershipSha1] = HashCodec.Format(PasswordAlgorithms.HashMembershipSha1("correct horse", salt)),
            [HashFormat.IdentityV2] = HashCodec.Format(PasswordAlgorithms.HashIdentityV2("correct horse", salt)),
            [HashFormat.IdentityV3] = HashCodec.Format(PasswordAlgorithms.HashIdentityV3("correct horse", salt, 10000)),
            [HashFormat.Argon2id] = HashCodec.Format(PasswordAlgorithms.HashArgon2id(
                "correct horse", salt, policy.MemoryKib, policy.Iterations, policy.Parallelism)),
        };

        // Warm every path first. A JIT compilation on the first Argon2id call would show up
        // as a timing difference that has nothing to do with the algorithm.
        foreach (var hash in stored.Values) hasher.Verify(hash, "wrong");

        var samples = new List<TimingSample>();
        for (var i = 0; i < samplesPerFormat; i++)
        {
            foreach (var (format, hash) in stored)
            {
                var clock = Stopwatch.StartNew();
                hasher.Verify(hash, "wrong guess");
                clock.Stop();
                samples.Add(new TimingSample(format, clock.Elapsed.TotalMilliseconds));
            }
        }

        // Train on the first half, evaluate on the second. Fitting and scoring on the same
        // samples would flatter the attacker.
        var half = samplesPerFormat / 2;
        var training = samples.Take(half * stored.Count).ToList();
        var evaluation = samples.Skip(half * stored.Count).ToList();

        var medians = training
            .GroupBy(s => s.Format)
            .ToDictionary(g => g.Key, g => Median(g.Select(s => s.Milliseconds)));

        var fourWayCorrect = 0;
        var binaryCorrect = 0;
        foreach (var sample in evaluation)
        {
            var predicted = medians
                .OrderBy(m => Math.Abs(m.Value - sample.Milliseconds))
                .First().Key;
            if (predicted == sample.Format) fourWayCorrect++;

            var predictedUnmigrated = predicted != HashFormat.Argon2id;
            var actuallyUnmigrated = sample.Format != HashFormat.Argon2id;
            if (predictedUnmigrated == actuallyUnmigrated) binaryCorrect++;
        }

        return new TimingChannelResult(
            constantWork,
            samples.GroupBy(s => s.Format).ToDictionary(g => g.Key, g => Median(g.Select(s => s.Milliseconds))),
            (double)fourWayCorrect / evaluation.Count,
            (double)binaryCorrect / evaluation.Count,
            Median(samples.Select(s => s.Milliseconds)));
    }

    /// <summary>
    /// Demonstrates that a fully migrated account is still reachable through the legacy
    /// ticket path, and that switching that path off is what closes it.
    /// </summary>
    /// <remarks>
    /// The scenario assumes the legacy validation key has leaked. That is not a stretch: it
    /// lives in web.config, it is identical across the farm, it is in every configuration
    /// backup, and in a system this old it is very often in source control history. Its
    /// blast radius is the question, and the answer is "every account the bridge will accept
    /// a ticket for", regardless of how strong that account's password now is.
    /// </remarks>
    public static (bool ReachableDuringCoexistence, bool ReachableAfterCutoff, string Subject)
        DowngradeExposure()
    {
        var now = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        var validationKey = RandomNumberGenerator.GetBytes(32);
        var protector = new LegacyTicketProtector(encryptionKey, validationKey);

        var directory = new UserDirectory();
        var hasher = new MigratingPasswordHasher(HashPolicy.Fast);
        directory.Add(new UserRecord
        {
            Subject = "finance.director",
            Tenant = "hq",
            Roles = new HashSet<string>([LegacyRoles.Admin], StringComparer.Ordinal),
            // Already fully migrated: strongest credential the system can issue.
            PasswordHash = hasher.HashNew("a-very-long-and-unguessable-passphrase"),
            RehashedAt = now.AddMonths(-6),
        });

        using var rsa = RSA.Create(2048);
        var federation = new WsFederation(rsa, "urn:legacy-sts");
        var jwt = new JwtCodec(RandomNumberGenerator.GetBytes(32));

        var bridge = new SessionBridge(
            directory, protector, federation, jwt,
            new DenyAwareClaimsTransformer(), hasher,
            StackConfiguration.Coexistence);

        // A ticket minted with the leaked key. The bridge has no way to tell it from one the
        // legacy application issued, because there is no difference.
        var forged = protector.Protect(new FormsTicket(
            2, "finance.director", now.AddMinutes(-1), now.AddHours(8),
            IsPersistent: true, UserData: LegacyRoles.Admin, CookiePath: "/"));

        var during = bridge.FromFormsTicket(forged, now);

        bridge.Configuration = StackConfiguration.After;
        var after = bridge.FromFormsTicket(forged, now);

        return (during.Ok, after.Ok, "finance.director");
    }

    /// <summary>
    /// Checks whether the hardened protector still lets a caller distinguish rejection
    /// reasons -- the property that decides whether it is an oracle.
    /// </summary>
    public static (IReadOnlyList<TicketFailure> Legacy, IReadOnlyList<TicketFailure> Hardened)
        RejectionReasons()
    {
        var now = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        var validationKey = RandomNumberGenerator.GetBytes(32);

        var legacy = new LegacyTicketProtector(encryptionKey, validationKey);
        var hardened = new HardenedTicketProtector(encryptionKey, validationKey);

        var ticket = new FormsTicket(
            2, "clerk.01", now.AddMinutes(-5), now.AddHours(1), false, LegacyRoles.Clerk, "/");

        IReadOnlyList<TicketFailure> Probe(ITicketProtector protector)
        {
            var good = protector.Protect(ticket);
            var bytes = Convert.FromHexString(good);

            var reasons = new List<TicketFailure>();

            // Flip a byte in the last ciphertext block: changes the plaintext padding.
            var padded = (byte[])bytes.Clone();
            padded[^1] ^= 0xFF;
            reasons.Add(protector.Unprotect(Convert.ToHexString(padded), now).Failure);

            // Flip a byte early in the ciphertext: padding survives, integrity does not.
            var body = (byte[])bytes.Clone();
            body[20] ^= 0x01;
            reasons.Add(protector.Unprotect(Convert.ToHexString(body), now).Failure);

            // Truncate.
            reasons.Add(protector.Unprotect(Convert.ToHexString(bytes[..(bytes.Length / 2)]), now).Failure);

            // Not hex at all.
            reasons.Add(protector.Unprotect("not-a-ticket", now).Failure);

            return reasons;
        }

        return (Probe(legacy), Probe(hardened));
    }

    /// <summary>
    /// The token-level hardening checks, run as a battery so the report can state a count
    /// rather than a list of adjectives.
    /// </summary>
    public static IReadOnlyList<(string Name, bool Rejected)> TokenHardening()
    {
        var now = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var results = new List<(string, bool)>();

        using var rsa = RSA.Create(2048);
        var rsaCodec = new JwtCodec(rsa);
        var claims = new System.Text.Json.Nodes.JsonObject
        {
            ["iss"] = "https://login.example",
            ["sub"] = "clerk.01",
            ["aud"] = "ledger-app",
            ["exp"] = now.AddMinutes(15).ToUnixTimeSeconds(),
            ["nbf"] = now.ToUnixTimeSeconds(),
        };
        var token = rsaCodec.Encode(claims);

        // alg: none -- signature stripped, header rewritten.
        var parts = token.Split('.');
        var noneHeader = JwtCodec.Base64Url(
            System.Text.Encoding.UTF8.GetBytes("""{"alg":"none","typ":"JWT"}"""));
        var noneToken = $"{noneHeader}.{parts[1]}.";
        results.Add(("alg=none is rejected",
            !rsaCodec.Validate(noneToken, "https://login.example", "ledger-app", now).Ok));

        // Algorithm confusion: present the RSA public key to an HMAC verifier as the secret.
        var publicKey = rsa.ExportSubjectPublicKeyInfo();
        var hmacCodec = new JwtCodec(publicKey);
        var confused = hmacCodec.Encode(claims);
        results.Add(("HS256 token is rejected by an RS256 validator",
            !rsaCodec.Validate(confused, "https://login.example", "ledger-app", now).Ok));

        // Audience belonging to a sibling application.
        var siblingClaims = (System.Text.Json.Nodes.JsonObject)claims.DeepClone();
        siblingClaims["aud"] = "reporting-app";
        results.Add(("token for another audience is rejected",
            !rsaCodec.Validate(rsaCodec.Encode(siblingClaims), "https://login.example", "ledger-app", now).Ok));

        // Expiry.
        results.Add(("expired token is rejected",
            !rsaCodec.Validate(token, "https://login.example", "ledger-app", now.AddHours(1)).Ok));

        // Nonce binding.
        results.Add(("id_token with the wrong nonce is rejected",
            !rsaCodec.Validate(token, "https://login.example", "ledger-app", now, "expected-nonce").Ok));

        // The happy path still works, which is the check that stops all of the above from
        // passing for the wrong reason.
        results.Add(("a valid token is accepted",
            rsaCodec.Validate(token, "https://login.example", "ledger-app", now).Ok));

        // PKCE.
        var authority = new OidcAuthority(rsaCodec, "https://login.example",
            [new OidcClient("ledger-app", ["https://ledger.example/cb"])]);
        var verifier = OidcAuthority.CreateVerifier();
        var challenge = OidcAuthority.Challenge(verifier);

        var (code, _) = authority.Authorize(
            "ledger-app", "https://ledger.example/cb", "n-1", challenge, "S256", "clerk.01", [], now);

        results.Add(("code redemption with the wrong verifier is rejected",
            !authority.Redeem(code!, "ledger-app", "https://ledger.example/cb",
                OidcAuthority.CreateVerifier(), now).Ok));

        results.Add(("code redemption with the right verifier succeeds",
            authority.Redeem(code!, "ledger-app", "https://ledger.example/cb", verifier, now).Ok));

        results.Add(("replaying a redeemed code is rejected",
            !authority.Redeem(code!, "ledger-app", "https://ledger.example/cb", verifier, now).Ok));

        var (_, plainFailure) = authority.Authorize(
            "ledger-app", "https://ledger.example/cb", "n-2", "verbatim", "plain", "clerk.01", [], now);
        results.Add(("challenge method 'plain' is refused",
            plainFailure == OidcFailure.UnsupportedChallengeMethod));

        var (_, redirectFailure) = authority.Authorize(
            "ledger-app", "https://ledger.example.attacker.test/cb", "n-3", challenge, "S256",
            "clerk.01", [], now);
        results.Add(("an unregistered redirect_uri is refused",
            redirectFailure == OidcFailure.RedirectUriMismatch));

        return results;
    }

    /// <summary>
    /// Replays a WS-Federation assertion and confirms the second use is refused.
    /// </summary>
    public static IReadOnlyList<(string Name, bool Rejected)> FederationHardening()
    {
        var now = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        using var rsa = RSA.Create(2048);
        var federation = new WsFederation(rsa, "urn:legacy-sts");

        var assertion = new SamlAssertion(
            "_a1", "urn:legacy-sts", "clerk.01", now.AddMinutes(-1), now.AddMinutes(30),
            "ledger-app", [new KeyValuePair<string, string>(ClaimNames.LegacyRole, LegacyRoles.Clerk)]);

        var xml = federation.Issue(assertion);

        var results = new List<(string, bool)>
        {
            ("a valid assertion is accepted", federation.Validate(xml, "ledger-app", now).Ok),
            ("replaying an assertion is rejected",
                federation.Validate(xml, "ledger-app", now).Failure == SamlFailure.Replayed),
        };

        var fresh = new WsFederation(rsa, "urn:legacy-sts");

        // An extra element the parser has no rule for.
        var wrapped = xml.Replace("</Assertion>",
            "<Attribute AttributeName=\"injected\">x</Attribute></Assertion>", StringComparison.Ordinal);
        results.Add(("an assertion with an appended attribute is rejected",
            !fresh.Validate(wrapped, "ledger-app", now).Ok));

        // A role added to an otherwise valid, correctly signed assertion.
        var escalated = xml.Replace(
            $">{LegacyRoles.Clerk}<", $">{LegacyRoles.Admin}<", StringComparison.Ordinal);
        results.Add(("an assertion with an edited role is rejected",
            fresh.Validate(escalated, "ledger-app", now).Failure == SamlFailure.BadSignature));

        var otherAudience = new WsFederation(rsa, "urn:legacy-sts");
        results.Add(("an assertion minted for another relying party is rejected",
            otherAudience.Validate(xml, "reporting-app", now).Failure == SamlFailure.WrongAudience));

        var expired = new WsFederation(rsa, "urn:legacy-sts");
        results.Add(("an expired assertion is rejected",
            expired.Validate(xml, "ledger-app", now.AddHours(2)).Failure == SamlFailure.Expired));

        return results;
    }

    /// <summary>
    /// End-to-end proof that a legacy credential ends up with the same principal a modern one
    /// produces -- which is the functional requirement, and simultaneously the security
    /// problem quantified by <see cref="DowngradeExposure"/>.
    /// </summary>
    public static bool ThreeStacksAgree()
    {
        var now = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);

        var encryptionKey = RandomNumberGenerator.GetBytes(32);
        var validationKey = RandomNumberGenerator.GetBytes(32);
        var protector = new HardenedTicketProtector(encryptionKey, validationKey);

        using var rsa = RSA.Create(2048);
        var federation = new WsFederation(rsa, "urn:legacy-sts");
        var jwt = new JwtCodec(rsa);

        var roles = new HashSet<string>([LegacyRoles.Manager, LegacyRoles.Clerk], StringComparer.Ordinal);
        var directory = new UserDirectory();
        var hasher = new MigratingPasswordHasher(HashPolicy.Fast);
        directory.Add(new UserRecord
        {
            Subject = "a.morgan",
            Tenant = "hq",
            Roles = roles,
            PasswordHash = hasher.HashNew("pass"),
        });

        var bridge = new SessionBridge(
            directory, protector, federation, jwt,
            new DenyAwareClaimsTransformer(), hasher, StackConfiguration.Coexistence);

        var fromForms = bridge.FromFormsTicket(
            protector.Protect(new FormsTicket(
                2, "a.morgan", now.AddMinutes(-1), now.AddHours(1), false,
                string.Join('|', roles), "/")),
            now);

        var samlXml = federation.Issue(new SamlAssertion(
            "_b2", "urn:legacy-sts", "a.morgan", now.AddMinutes(-1), now.AddMinutes(30), "ledger-app",
            roles.Select(r => new KeyValuePair<string, string>(ClaimNames.LegacyRole, r)).ToList()));
        var fromSaml = bridge.FromWsFederation(samlXml, "ledger-app", now);

        var authority = new OidcAuthority(jwt, "https://login.example",
            [new OidcClient("ledger-app", ["https://ledger.example/cb"])]);
        var verifier = OidcAuthority.CreateVerifier();
        var (code, _) = authority.Authorize(
            "ledger-app", "https://ledger.example/cb", "n", OidcAuthority.Challenge(verifier),
            "S256", "a.morgan", [], now);
        var tokens = authority.Redeem(code!, "ledger-app", "https://ledger.example/cb", verifier, now);
        var fromOidc = bridge.FromIdToken(tokens.Tokens!.IdToken, "https://login.example", "ledger-app", now, "n");

        if (!fromForms.Ok || !fromSaml.Ok || !fromOidc.Ok) return false;

        var principals = new[] { fromForms.Principal!, fromSaml.Principal!, fromOidc.Principal! };
        return principals.All(p =>
            p.Subject == "a.morgan" &&
            p.Scopes.SetEquals(principals[0].Scopes) &&
            p.DenyScopes.SetEquals(principals[0].DenyScopes));
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToArray();
        if (sorted.Length == 0) return 0;
        return sorted.Length % 2 == 1
            ? sorted[sorted.Length / 2]
            : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2.0;
    }
}
