using Auth.Passwords;

namespace Auth.Tests;

/// <summary>
/// The RFC 9106 vectors, plus the interim block values the RFC prints alongside them.
///
/// The interim values are the point of this file. Both bugs found while writing this
/// implementation produced a final tag that was wrong but structurally plausible, and
/// neither could be localised from the tag alone. Comparing block 0 and block 31 after
/// each pass turns "the answer is wrong" into "the answer first goes wrong in pass 0,
/// at the end of the first slice", which is a debuggable statement.
/// </summary>
public sealed class Argon2Tests
{
    // RFC 9106 section 5: P = 32x0x01, S = 16x0x02, K = 8x0x03, X = 12x0x04,
    // m = 32 KiB, t = 3, p = 4, tag length 32.
    private static readonly byte[] Password = Enumerable.Repeat((byte)0x01, 32).ToArray();
    private static readonly byte[] Salt = Enumerable.Repeat((byte)0x02, 16).ToArray();
    private static readonly byte[] Secret = Enumerable.Repeat((byte)0x03, 8).ToArray();
    private static readonly byte[] AssociatedData = Enumerable.Repeat((byte)0x04, 12).ToArray();

    private const string Argon2dTag = "512b391b6f1162975371d30919734294f868e3be3984f3c1a13a4db9fabe4acb";
    private const string Argon2iTag = "c814d9d1dc7f37aa13f0d77f2494bda1c8de6b016dd388d29952a4c4672b6ce8";
    private const string Argon2idTag = "0d640df58d78766c08c037a34a8b53c9d01ef0452d75b65eb52520e96b01e659";

    private const string ExpectedH0 =
        "b8819791a0359660bb7709c85fa48f04d5d82c05c5f215ccdb885491717cf757"
      + "082c28b951be381410b5fc2eb7274033b9fdc7ae672bcaac5d179097a4af3109";

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    private static byte[] RfcHash(Argon2Type type, Argon2Trace? trace = null) =>
        Argon2.Hash(Password, Salt, 32, 3, 4, 32, type, Secret, AssociatedData, trace);

    [Theory]
    [InlineData(Argon2Type.Argon2d, Argon2dTag)]
    [InlineData(Argon2Type.Argon2i, Argon2iTag)]
    [InlineData(Argon2Type.Argon2id, Argon2idTag)]
    public void MatchesRfc9106TestVector(Argon2Type type, string expected)
    {
        Assert.Equal(expected, Hex(RfcHash(type)));
    }

    [Fact]
    public void H0MatchesTheRfcInterimValue()
    {
        // H0 covers every parameter, including the ones that are easiest to forget: the
        // version number, the secret and the associated data. If H0 is right, the inputs
        // were assembled and length-prefixed correctly and any remaining fault is in the
        // filling.
        var trace = new Argon2Trace();
        RfcHash(Argon2Type.Argon2d, trace);
        Assert.Equal(ExpectedH0, Hex(trace.PreHashingDigest));
    }

    // Pass -> (first word of block 0, last word of block 31), from RFC 9106's Argon2d trace.
    [Theory]
    [InlineData(0, 0xdb2fea6b2c6f5c8aUL, 0x6a6c49d2cb75d5b6UL)]
    [InlineData(1, 0xd3801200410f8c0dUL, 0x2dbfff23f31b5883UL)]
    [InlineData(2, 0x5f047b575c5ff4d2UL, 0xc341b3ca45c10da5UL)]
    public void InterimBlocksMatchTheRfcTrace(int pass, ulong firstWordBlock0, ulong lastWordBlock31)
    {
        var trace = new Argon2Trace();
        RfcHash(Argon2Type.Argon2d, trace);

        Assert.Equal(firstWordBlock0, trace.Block(pass, 0)[0]);
        Assert.Equal(lastWordBlock31, trace.Block(pass, 31)[127]);
    }

    [Fact]
    public void Block31DivergesEvenWhenBlock0Agrees()
    {
        // This is the regression test for the variable-length hash bug. Block 0's first word
        // is derived from A_1, which the faulty tail never touched, so block 0 matched while
        // everything downstream was wrong. A test that only checked block 0 would have
        // reported success on a broken implementation.
        var trace = new Argon2Trace();
        RfcHash(Argon2Type.Argon2d, trace);

        Assert.Equal(0xdb2fea6b2c6f5c8aUL, trace.Block(0, 0)[0]);
        Assert.Equal(0x6a6c49d2cb75d5b6UL, trace.Block(0, 31)[127]);
    }

    [Fact]
    public void PreHashingDigestDependsOnTheVariant()
    {
        // The type byte y is inside H0, so the three variants do not merely address memory
        // differently -- they start from different memory. No two of them share even block 0,
        // which is worth knowing before writing a test that assumes otherwise.
        var d = new Argon2Trace();
        var i = new Argon2Trace();
        var id = new Argon2Trace();
        RfcHash(Argon2Type.Argon2d, d);
        RfcHash(Argon2Type.Argon2i, i);
        RfcHash(Argon2Type.Argon2id, id);

        var digests = new[] { Hex(d.PreHashingDigest), Hex(i.PreHashingDigest), Hex(id.PreHashingDigest) };
        Assert.Equal(3, digests.Distinct().Count());
        Assert.Equal(3, new[] { d.Block(0, 0)[0], i.Block(0, 0)[0], id.Block(0, 0)[0] }.Distinct().Count());
    }

    [Fact]
    public void AllThreeVariantsDivergeByTheEndOfPassZero()
    {
        var d = new Argon2Trace();
        var i = new Argon2Trace();
        var id = new Argon2Trace();
        RfcHash(Argon2Type.Argon2d, d);
        RfcHash(Argon2Type.Argon2i, i);
        RfcHash(Argon2Type.Argon2id, id);

        var last = new[] { d.Block(0, 31)[127], i.Block(0, 31)[127], id.Block(0, 31)[127] };
        Assert.Equal(3, last.Distinct().Count());
    }

    [Fact]
    public void IsDeterministic()
    {
        Assert.Equal(Hex(RfcHash(Argon2Type.Argon2id)), Hex(RfcHash(Argon2Type.Argon2id)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(128)]
    public void HonoursRequestedTagLength(int tagLength)
    {
        var tag = Argon2.Hash(Password, Salt, 32, 1, 1, tagLength);
        Assert.Equal(tagLength, tag.Length);
    }

    [Fact]
    public void TagLengthIsNotATruncation()
    {
        // H' mixes the requested length into its input, so a 64-byte tag does not begin with
        // the 32-byte tag. This is the property the faulty tail loop happened to preserve.
        var thirtyTwo = Hex(Argon2.Hash(Password, Salt, 32, 1, 1, 32));
        var sixtyFour = Hex(Argon2.Hash(Password, Salt, 32, 1, 1, 64));
        Assert.NotEqual(thirtyTwo, sixtyFour[..64]);
    }

    [Fact]
    public void LongTagsCrossTheVariableLengthHashBoundary()
    {
        // H' switches from "one Blake2b call" to the iterated construction above 64 bytes.
        // 1024 is an exact multiple of 32, which is where the off-by-one tail bug hid: the
        // final chunk is 64 bytes by coincidence rather than by construction.
        var a = Argon2.Hash(Password, Salt, 32, 1, 1, 1024);
        var b = Argon2.Hash(Password, Salt, 32, 1, 1, 1025);
        Assert.Equal(1024, a.Length);
        Assert.Equal(1025, b.Length);
        Assert.NotEqual(Hex(a), Hex(b)[..2048]);
    }

    [Fact]
    public void DifferentSaltsGiveDifferentTags()
    {
        var a = Argon2.Hash(Password, Salt, 32, 1, 1, 32);
        var b = Argon2.Hash(Password, Enumerable.Repeat((byte)0x05, 16).ToArray(), 32, 1, 1, 32);
        Assert.NotEqual(Hex(a), Hex(b));
    }

    [Fact]
    public void SecretAndAssociatedDataChangeTheTag()
    {
        var plain = Argon2.Hash(Password, Salt, 32, 1, 1, 32);
        var keyed = Argon2.Hash(Password, Salt, 32, 1, 1, 32, Argon2Type.Argon2id, Secret);
        var tagged = Argon2.Hash(Password, Salt, 32, 1, 1, 32, Argon2Type.Argon2id,
                                 default, AssociatedData);

        Assert.NotEqual(Hex(plain), Hex(keyed));
        Assert.NotEqual(Hex(plain), Hex(tagged));
        Assert.NotEqual(Hex(keyed), Hex(tagged));
    }

    [Fact]
    public void RoundingMemoryDownDoesNotMakeTagsInterchangeable()
    {
        // m' = 4 * p * floor(m / 4p), so with p=1 a request for 11 KiB and a request for
        // 8 KiB fill exactly the same memory. The tags still differ, because H0 commits to
        // the *requested* m rather than the rounded m'.
        //
        // That combination is a trap. An operator who "increases" memory from 8 to 11 gets
        // no extra security whatsoever and invalidates every stored hash at the same time --
        // the worst of both outcomes, and neither half of it is visible without checking.
        var eight = new Argon2Trace();
        var eleven = new Argon2Trace();
        var eightTag = Argon2.Hash(Password, Salt, 8, 1, 1, 32, Argon2Type.Argon2id,
                                   default, default, eight);
        var elevenTag = Argon2.Hash(Password, Salt, 11, 1, 1, 32, Argon2Type.Argon2id,
                                    default, default, eleven);

        var eightBlocks = eight.Blocks.Max(b => b.BlockIndex) + 1;
        var elevenBlocks = eleven.Blocks.Max(b => b.BlockIndex) + 1;

        Assert.Equal(8, eightBlocks);
        Assert.Equal(8, elevenBlocks);
        Assert.NotEqual(Hex(eightTag), Hex(elevenTag));
    }

    [Fact]
    public void MemoryRoundsDownToAMultipleOfFourTimesParallelism()
    {
        var trace = new Argon2Trace();
        Argon2.Hash(Password, Salt, 47, 1, 4, 32, Argon2Type.Argon2id, default, default, trace);

        // floor(47 / 16) * 16 = 32.
        Assert.Equal(32, trace.Blocks.Max(b => b.BlockIndex) + 1);
    }

    [Theory]
    [InlineData(0, 1, 1, 32)]   // parallelism 0
    [InlineData(1, 0, 1, 32)]   // iterations 0
    [InlineData(1, 1, 1, 3)]    // tag under 4 bytes
    public void RejectsOutOfRangeParameters(int parallelism, int iterations, int _, int tagLength)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Argon2.Hash(Password, Salt, 32, iterations, parallelism, tagLength));
    }

    [Fact]
    public void RejectsMemoryBelowEightTimesParallelism()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Argon2.Hash(Password, Salt, 7, 1, 1, 32));
    }

    [Fact]
    public void MoreIterationsChangeTheTag()
    {
        Assert.NotEqual(
            Hex(Argon2.Hash(Password, Salt, 32, 1, 1, 32)),
            Hex(Argon2.Hash(Password, Salt, 32, 2, 1, 32)));
    }

    [Fact]
    public void MoreLanesChangeTheTag()
    {
        Assert.NotEqual(
            Hex(Argon2.Hash(Password, Salt, 32, 1, 1, 32)),
            Hex(Argon2.Hash(Password, Salt, 32, 1, 2, 32)));
    }

    [Fact]
    public void EmptySaltIsAccepted()
    {
        // RFC 9106 requires 16 bytes for password hashing but the primitive itself is
        // defined for any salt. The policy check belongs in the hasher, not here.
        var tag = Argon2.Hash(Password, ReadOnlySpan<byte>.Empty, 32, 1, 1, 32);
        Assert.Equal(32, tag.Length);
    }
}
