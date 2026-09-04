using Auth.Passwords;

namespace Auth.Tests;

/// <summary>
/// Rehash-on-login. The mechanism is three lines long and every one of them is a place
/// a migration goes wrong: verifying against the wrong scheme, deciding "current" too
/// generously, and answering "does this account exist" through the clock.
/// </summary>
public sealed class MigratingPasswordHasherTests
{
    private static readonly HashPolicy Fast = HashPolicy.Fast;

    private static MigratingPasswordHasher Hasher(bool constantWork = false) =>
        new(Fast, constantWork);

    private static string Legacy(string password) =>
        HashCodec.Format(PasswordAlgorithms.HashMembershipSha1(
            password, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()));

    private static string IdentityV2(string password) =>
        HashCodec.Format(PasswordAlgorithms.HashIdentityV2(
            password, Enumerable.Range(0, 16).Select(i => (byte)i).ToArray()));

    [Fact]
    public void VerifiesALegacyHashAndHandsBackAnUpgrade()
    {
        var result = Hasher().Verify(Legacy("hunter2"), "hunter2");

        Assert.Equal(VerifyOutcome.SucceededNeedsRehash, result.Outcome);
        Assert.Equal(HashFormat.MembershipSha1, result.StoredFormat);
        Assert.NotNull(result.UpgradedHash);
        Assert.Equal(HashFormat.Argon2id, HashCodec.Parse(result.UpgradedHash!).Format);
    }

    [Fact]
    public void TheUpgradedHashVerifiesTheSamePassword()
    {
        // Obvious, and the single most valuable assertion in the file: an upgrade that does
        // not verify locks the user out on their *next* login, one request after the point
        // where anything is still being logged about it.
        var hasher = Hasher();
        var upgraded = hasher.Verify(Legacy("hunter2"), "hunter2").UpgradedHash!;

        Assert.Equal(VerifyOutcome.SucceededCurrent, hasher.Verify(upgraded, "hunter2").Outcome);
        Assert.Equal(VerifyOutcome.Failed, hasher.Verify(upgraded, "hunter3").Outcome);
    }

    [Fact]
    public void DoesNotUpgradeAHashThatAlreadyMeetsPolicy()
    {
        var hasher = Hasher();
        var current = hasher.HashNew("hunter2");
        var result = hasher.Verify(current, "hunter2");

        Assert.Equal(VerifyOutcome.SucceededCurrent, result.Outcome);
        Assert.Null(result.UpgradedHash);
    }

    [Fact]
    public void WrongPasswordNeverProducesAnUpgrade()
    {
        // An upgrade path that fires before the password is confirmed would let anyone
        // overwrite anyone's credential with one of their choosing.
        var result = Hasher().Verify(Legacy("hunter2"), "hunter3");

        Assert.Equal(VerifyOutcome.Failed, result.Outcome);
        Assert.Null(result.UpgradedHash);
    }

    [Fact]
    public void UnparseableStoredHashFailsRatherThanThrows()
    {
        // Real credential columns contain nulls, empty strings, and the residue of a
        // half-finished import from 2013. A login endpoint that throws on those turns a
        // data-quality problem into a 500 and a pager.
        foreach (var stored in new[] { null, "", "   ", "garbage" })
        {
            var result = Hasher().Verify(stored, "hunter2");
            Assert.Equal(VerifyOutcome.Failed, result.Outcome);
            Assert.False(result.Succeeded);
        }
    }

    [Fact]
    public void NeedsRehashIsDrivenByPolicyNotByFormat()
    {
        // "Is it Argon2id" is not the question. An Argon2id hash produced under last year's
        // parameters is exactly as much in need of an upgrade as a PBKDF2 one, and a check
        // written as a format comparison will never notice.
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var hasher = new MigratingPasswordHasher(new HashPolicy(MemoryKib: 1024, Iterations: 2,
                                                                Parallelism: 1));

        Assert.True(hasher.NeedsRehash(PasswordAlgorithms.HashArgon2id("x", salt, 512, 2, 1)));
        Assert.True(hasher.NeedsRehash(PasswordAlgorithms.HashArgon2id("x", salt, 1024, 1, 1)));
        Assert.False(hasher.NeedsRehash(PasswordAlgorithms.HashArgon2id("x", salt, 1024, 2, 1)));
        Assert.False(hasher.NeedsRehash(PasswordAlgorithms.HashArgon2id("x", salt, 2048, 3, 1)));
    }

    [Theory]
    [InlineData(HashFormat.MembershipSha1)]
    [InlineData(HashFormat.IdentityV2)]
    [InlineData(HashFormat.IdentityV3)]
    public void EveryLegacyFormatNeedsRehash(HashFormat format)
    {
        var salt = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        var stored = format switch
        {
            HashFormat.MembershipSha1 => PasswordAlgorithms.HashMembershipSha1("x", salt),
            HashFormat.IdentityV2 => PasswordAlgorithms.HashIdentityV2("x", salt),
            _ => PasswordAlgorithms.HashIdentityV3("x", salt, 100_000, Prf.HmacSha512),
        };

        Assert.True(Hasher().NeedsRehash(stored));
    }

    [Fact]
    public void HashNewProducesADistinctSaltEveryTime()
    {
        var hasher = Hasher();
        var hashes = Enumerable.Range(0, 20).Select(_ => hasher.HashNew("hunter2")).ToArray();

        Assert.Equal(20, hashes.Distinct().Count());
        Assert.All(hashes, h => Assert.True(PasswordAlgorithms.Verify(HashCodec.Parse(h), "hunter2")));
    }

    [Fact]
    public void AnInjectedSaltSourceMakesHashingDeterministic()
    {
        // Not a production feature -- it exists so the report can produce byte-identical
        // output, and so a test can assert on a hash rather than on the fact that two
        // random hashes differ.
        var fixedSalt = new MigratingPasswordHasher(Fast, false, n => new byte[n]);
        Assert.Equal(fixedSalt.HashNew("hunter2"), fixedSalt.HashNew("hunter2"));
    }

    [Fact]
    public void UnknownUserIsVerifiedAgainstADecoy()
    {
        // The stored hash is null, but the work still happens. Without this, "user not
        // found" returns in microseconds and "wrong password" returns in a third of a
        // second, and the login endpoint has become a user enumeration oracle that no
        // amount of rate limiting closes.
        var hasher = Hasher();
        var absent = hasher.Verify(null, "hunter2");
        var present = hasher.Verify(hasher.HashNew("something-else"), "hunter2");

        Assert.Equal(VerifyOutcome.Failed, absent.Outcome);
        Assert.Equal(VerifyOutcome.Failed, present.Outcome);
        Assert.True(absent.ElapsedMilliseconds > 0);
    }

    [Fact]
    public void ConstantWorkIsOptOut()
    {
        Assert.False(Hasher().ConstantWork);
        Assert.True(Hasher(constantWork: true).ConstantWork);
    }

    [Fact]
    public void ConstantWorkDoesNotChangeAnyVerdict()
    {
        // The padding must be invisible in the answer and visible only in the clock.
        // A mitigation that changes an authentication outcome is a bug, not a mitigation.
        var padded = Hasher(constantWork: true);
        var plain = Hasher();

        foreach (var (stored, password) in new (string?, string)[]
                 {
                     (Legacy("hunter2"), "hunter2"),
                     (Legacy("hunter2"), "wrong"),
                     (IdentityV2("hunter2"), "hunter2"),
                     (IdentityV2("hunter2"), "wrong"),
                     (null, "hunter2"),
                     ("garbage", "hunter2"),
                 })
        {
            Assert.Equal(plain.Verify(stored, password).Outcome,
                         padded.Verify(stored, password).Outcome);
        }
    }

    [Fact]
    public void ConstantWorkStillUpgradesLegacyHashes()
    {
        var result = Hasher(constantWork: true).Verify(Legacy("hunter2"), "hunter2");
        Assert.Equal(VerifyOutcome.SucceededNeedsRehash, result.Outcome);
        Assert.NotNull(result.UpgradedHash);
    }

    [Fact]
    public void PolicyDefaultsToTheDeployableRfcProfile()
    {
        // RFC 9106's *second* recommended option. The first (2 GiB) is the one people quote
        // and nobody deploys, because a login endpoint holding 2 GiB per concurrent request
        // is a denial of service discovered by ordinary Monday morning traffic.
        Assert.Equal(65536, HashPolicy.Recommended.MemoryKib);
        Assert.Equal(3, HashPolicy.Recommended.Iterations);
        Assert.Equal(4, HashPolicy.Recommended.Parallelism);
        Assert.Equal(16, HashPolicy.Recommended.SaltBytes);
        Assert.Equal(32, HashPolicy.Recommended.DigestBytes);
    }

    [Fact]
    public void ReportsWhichFormatItFound()
    {
        // The migration cannot be measured without this. "How many users are still on
        // SHA-1" is answerable only if every verification says what it found.
        Assert.Equal(HashFormat.MembershipSha1, Hasher().Verify(Legacy("p"), "p").StoredFormat);
        Assert.Equal(HashFormat.IdentityV2, Hasher().Verify(IdentityV2("p"), "p").StoredFormat);
    }

    [Fact]
    public void ReportsTheFormatEvenOnFailure()
    {
        // Useful for the operator and, inconveniently, for the attacker. Whether this is
        // surfaced beyond the log is a decision the caller has to make deliberately.
        Assert.Equal(HashFormat.MembershipSha1, Hasher().Verify(Legacy("p"), "wrong").StoredFormat);
    }
}
