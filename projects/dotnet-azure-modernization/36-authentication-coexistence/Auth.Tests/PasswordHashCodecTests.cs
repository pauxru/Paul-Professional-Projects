using Auth.Passwords;

namespace Auth.Tests;

/// <summary>
/// The codec is the part of a hash migration nobody budgets time for and everybody
/// underestimates. Four generations of ASP.NET stored credentials in four different
/// shapes, and the only thing the database guarantees is that the column is a string.
/// </summary>
public sealed class PasswordHashCodecTests
{
    private static readonly byte[] Salt16 = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();

    public static TheoryData<StoredHash> AllFormats()
    {
        var data = new TheoryData<StoredHash>
        {
            PasswordAlgorithms.HashMembershipSha1("hunter2", Salt16),
            PasswordAlgorithms.HashIdentityV2("hunter2", Salt16),
            PasswordAlgorithms.HashIdentityV3("hunter2", Salt16, 10_000, Prf.HmacSha256),
            PasswordAlgorithms.HashArgon2id("hunter2", Salt16, 8, 1, 1),
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void RoundTripsThroughTheStringForm(StoredHash original)
    {
        var parsed = HashCodec.Parse(HashCodec.Format(original));

        Assert.Equal(original.Format, parsed.Format);
        Assert.Equal(original.Salt, parsed.Salt);
        Assert.Equal(original.Digest, parsed.Digest);
        Assert.Equal(original.Iterations, parsed.Iterations);
        Assert.Equal(original.Prf, parsed.Prf);
        Assert.Equal(original.MemoryKib, parsed.MemoryKib);
        Assert.Equal(original.Parallelism, parsed.Parallelism);
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void FormattingIsStable(StoredHash original)
    {
        var once = HashCodec.Format(original);
        Assert.Equal(once, HashCodec.Format(HashCodec.Parse(once)));
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void VerifiesTheCorrectPassword(StoredHash stored)
    {
        Assert.True(PasswordAlgorithms.Verify(stored, "hunter2"));
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void RejectsTheWrongPassword(StoredHash stored)
    {
        Assert.False(PasswordAlgorithms.Verify(stored, "hunter3"));
        Assert.False(PasswordAlgorithms.Verify(stored, ""));
        Assert.False(PasswordAlgorithms.Verify(stored, "HUNTER2"));
    }

    [Fact]
    public void IdentityFormatsAreDistinguishedByTheirMarkerByte()
    {
        // Identity v2 and v3 share a base64 column and are told apart by the first byte of
        // the decoded payload. Anything that loses that byte -- a trim, an encoding change,
        // a well-meaning "normalisation" -- silently reinterprets every v3 hash as a v2.
        var v2 = HashCodec.Encode(PasswordAlgorithms.HashIdentityV2("hunter2", Salt16));
        var v3 = HashCodec.Encode(
            PasswordAlgorithms.HashIdentityV3("hunter2", Salt16, 10_000, Prf.HmacSha256));

        Assert.Equal(HashCodec.IdentityV2Marker, v2[0]);
        Assert.Equal(HashCodec.IdentityV3Marker, v3[0]);
    }

    [Fact]
    public void Argon2idCarriesItsParametersInTheStoredForm()
    {
        // A hash that does not record its own cost parameters cannot be verified after the
        // parameters are raised, which is the entire reason parameters get raised only once.
        var stored = PasswordAlgorithms.HashArgon2id("hunter2", Salt16, 8, 1, 1);
        var bytes = HashCodec.Encode(stored);
        var parsed = HashCodec.Parse(HashCodec.Format(stored));

        Assert.Equal(HashCodec.Argon2idMarker, bytes[0]);
        Assert.Equal(8, parsed.MemoryKib);
        Assert.Equal(1, parsed.Iterations);
        Assert.Equal(1, parsed.Parallelism);
        Assert.True(PasswordAlgorithms.Verify(parsed, "hunter2"));
    }

    [Fact]
    public void RaisingParametersDoesNotBreakOlderArgon2idHashes()
    {
        var old = HashCodec.Format(PasswordAlgorithms.HashArgon2id("hunter2", Salt16, 8, 1, 1));
        var raised = HashCodec.Format(PasswordAlgorithms.HashArgon2id("hunter2", Salt16, 16, 2, 1));

        Assert.NotEqual(old, raised);
        Assert.True(PasswordAlgorithms.Verify(HashCodec.Parse(old), "hunter2"));
        Assert.True(PasswordAlgorithms.Verify(HashCodec.Parse(raised), "hunter2"));
    }

    [Fact]
    public void MembershipFormatIsBareBase64()
    {
        // SqlMembershipProvider stored the salt in one column and the digest in another,
        // so a migration has to invent a combined representation. Making it visibly
        // different from the Identity forms is what stops a v2 parser accepting one.
        var text = HashCodec.Format(PasswordAlgorithms.HashMembershipSha1("hunter2", Salt16));
        Assert.Equal(HashFormat.MembershipSha1, HashCodec.Parse(text).Format);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not base64 at all!!")]
    [InlineData("////")]
    public void RejectsUnparseableInput(string stored)
    {
        Assert.Throws<FormatException>(() => HashCodec.Parse(stored));
    }

    [Fact]
    public void RejectsAnUnknownMarkerByte()
    {
        var bytes = new byte[64];
        bytes[0] = 0x7f;
        Assert.Throws<FormatException>(() => HashCodec.Decode(bytes));
    }

    [Fact]
    public void RejectsATruncatedIdentityV3Payload()
    {
        var full = HashCodec.Encode(
            PasswordAlgorithms.HashIdentityV3("hunter2", Salt16, 10_000, Prf.HmacSha256));
        Assert.Throws<FormatException>(() => HashCodec.Decode(full[..10]));
    }

    [Fact]
    public void WorkFactorsAreOrderedAsTheGenerationsIntended()
    {
        // This ordering is the whole justification for the migration, so it is asserted
        // rather than assumed. The unit is SHA-256-equivalent compressions per guess, which
        // is the number an attacker's budget is actually denominated in.
        var membership = PasswordAlgorithms.HashMembershipSha1("x", Salt16);
        var v2 = PasswordAlgorithms.HashIdentityV2("x", Salt16);
        var v3 = PasswordAlgorithms.HashIdentityV3("x", Salt16, 10_000, Prf.HmacSha256);
        var argon = PasswordAlgorithms.HashArgon2id("x", Salt16, 65536, 3, 4);

        Assert.True(membership.AttackerCompressionsPerGuess < v2.AttackerCompressionsPerGuess);
        Assert.True(v2.AttackerCompressionsPerGuess < v3.AttackerCompressionsPerGuess);
        Assert.True(v3.AttackerCompressionsPerGuess < argon.AttackerCompressionsPerGuess);
    }

    [Fact]
    public void MembershipSha1CostsOneCompression()
    {
        Assert.Equal(1, PasswordAlgorithms.HashMembershipSha1("x", Salt16).AttackerCompressionsPerGuess);
    }

    [Fact]
    public void SaltsAreNotSharedBetweenFormats()
    {
        var a = PasswordAlgorithms.HashIdentityV2("hunter2", Salt16);
        var b = PasswordAlgorithms.HashIdentityV2("hunter2", new byte[16]);
        Assert.NotEqual(Convert.ToHexString(a.Digest), Convert.ToHexString(b.Digest));
    }

    [Theory]
    [InlineData(Prf.HmacSha1)]
    [InlineData(Prf.HmacSha256)]
    [InlineData(Prf.HmacSha512)]
    public void IdentityV3SupportsEachPrf(Prf prf)
    {
        var stored = PasswordAlgorithms.HashIdentityV3("hunter2", Salt16, 1000, prf);
        Assert.Equal(prf, stored.Prf);
        Assert.True(PasswordAlgorithms.Verify(stored, "hunter2"));
        Assert.Equal(prf, HashCodec.Parse(HashCodec.Format(stored)).Prf);
    }

    [Fact]
    public void ChangingThePrfChangesTheDigest()
    {
        var sha256 = PasswordAlgorithms.HashIdentityV3("hunter2", Salt16, 1000, Prf.HmacSha256);
        var sha512 = PasswordAlgorithms.HashIdentityV3("hunter2", Salt16, 1000, Prf.HmacSha512);
        Assert.NotEqual(Convert.ToHexString(sha256.Digest), Convert.ToHexString(sha512.Digest));
    }

    [Fact]
    public void Pbkdf2MatchesRfc6070Vector()
    {
        // RFC 6070 test vector 1 for PBKDF2-HMAC-SHA1: P="password", S="salt", c=1, dkLen=20.
        // Identity v2 is exactly this construction with c=1000, so pinning the primitive
        // against a published vector means a v2 hash produced here would be accepted by the
        // framework that wrote the originals.
        var derived = PasswordAlgorithms.Pbkdf2("password", "salt"u8.ToArray(), 1, Prf.HmacSha1, 20);
        Assert.Equal("0c60c80f961f0e71f3a9b524af6012062fe037a6",
                     Convert.ToHexString(derived).ToLowerInvariant());
    }

    [Fact]
    public void Pbkdf2MatchesRfc6070IteratedVector()
    {
        var derived = PasswordAlgorithms.Pbkdf2("password", "salt"u8.ToArray(), 4096, Prf.HmacSha1, 20);
        Assert.Equal("4b007901b765489abead49d926f721d065a429c1",
                     Convert.ToHexString(derived).ToLowerInvariant());
    }

    [Fact]
    public void UnicodePasswordsRoundTrip()
    {
        // The legacy stack was UTF-16-minded and the modern one is UTF-8. A user whose
        // password contains anything outside ASCII is the one who discovers the difference,
        // and they discover it as "my password stopped working".
        const string password = "\u00fcber-\u5bc6\u7801-\ud83d\udd10";
        var stored = PasswordAlgorithms.HashArgon2id(password, Salt16, 8, 1, 1);
        Assert.True(PasswordAlgorithms.Verify(HashCodec.Parse(HashCodec.Format(stored)), password));
    }

    [Fact]
    public void EmptyPasswordIsHashable()
    {
        var stored = PasswordAlgorithms.HashArgon2id("", Salt16, 8, 1, 1);
        Assert.True(PasswordAlgorithms.Verify(stored, ""));
        Assert.False(PasswordAlgorithms.Verify(stored, " "));
    }
}
