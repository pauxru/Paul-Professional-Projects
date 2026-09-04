using Auth.Passwords;

namespace Auth.Tests;

/// <summary>
/// Blake2b is the foundation everything else in Argon2 stands on. If it is wrong, every
/// Argon2 digest is wrong in a way that still looks like a plausible random string, so it
/// gets its own vectors from RFC 7693 rather than being tested only through Argon2.
/// </summary>
public sealed class Blake2bTests
{
    // RFC 7693 Appendix A: BLAKE2b-512("abc").
    private const string AbcDigest =
        "ba80a53f981c4d0d6a2797b69f12f6e94c212f14685ac4b74b12bb6fdbffa2d1"
      + "7d87c5392aab792dc252d5de4533cc9518d38aa8dbf1925ab92386edd4009923";

    // Empty input, 512-bit digest. From the reference implementation's test vectors.
    private const string EmptyDigest =
        "786a02f742015903c6c6fd852552d272912f4740e15847618a86e217f71f5419"
      + "d25e1031afee585313896444934eb04b903a685b1448b755d56f701afe9be2ce";

    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    [Fact]
    public void MatchesRfc7693AbcVector()
    {
        var digest = Blake2b.Hash("abc"u8, 64);
        Assert.Equal(AbcDigest, Hex(digest));
    }

    [Fact]
    public void MatchesEmptyInputVector()
    {
        Assert.Equal(EmptyDigest, Hex(Blake2b.Hash(ReadOnlySpan<byte>.Empty, 64)));
    }

    [Fact]
    public void StreamingUpdateMatchesOneShot()
    {
        var data = new byte[1000];
        for (var i = 0; i < data.Length; i++) data[i] = (byte)(i * 7 % 251);

        var oneShot = Blake2b.Hash(data, 64);

        var streaming = new Blake2b(64);
        streaming.Update(data.AsSpan(0, 1));
        streaming.Update(data.AsSpan(1, 127));
        streaming.Update(data.AsSpan(128, 128));   // exactly one block
        streaming.Update(data.AsSpan(256, 3));
        streaming.Update(data.AsSpan(259));
        Assert.Equal(Hex(oneShot), Hex(streaming.Finish()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(32)]
    [InlineData(63)]
    [InlineData(64)]
    public void HonoursRequestedDigestLength(int length)
    {
        Assert.Equal(length, Blake2b.Hash("abc"u8, length).Length);
    }

    [Fact]
    public void DigestLengthIsPartOfTheParameterBlock()
    {
        // A 32-byte digest is not a truncation of the 64-byte one: the length is mixed into
        // h[0]. Getting this wrong produces an implementation that passes a truncation test
        // and fails every real vector.
        var thirtyTwo = Hex(Blake2b.Hash("abc"u8, 32));
        var truncated = Hex(Blake2b.Hash("abc"u8, 64))[..64];
        Assert.NotEqual(truncated, thirtyTwo);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public void RejectsDigestLengthOutsideRange(int length)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Blake2b(length));
    }

    [Fact]
    public void RejectsOverlongKey()
    {
        Assert.Throws<ArgumentException>(() => new Blake2b(64, new byte[65]));
    }

    [Fact]
    public void KeyedHashDiffersFromUnkeyed()
    {
        var keyed = new Blake2b(64, new byte[] { 1, 2, 3 });
        keyed.Update("abc"u8);
        Assert.NotEqual(AbcDigest, Hex(keyed.Finish()));
    }

    [Fact]
    public void EmptyKeyBehavesAsUnkeyed()
    {
        var explicitlyEmpty = new Blake2b(64, ReadOnlySpan<byte>.Empty);
        explicitlyEmpty.Update("abc"u8);
        Assert.Equal(AbcDigest, Hex(explicitlyEmpty.Finish()));
    }

    [Fact]
    public void ExactBlockMultipleInputIsNotOverPadded()
    {
        // The final block must be compressed with the last-block flag even when the input is
        // an exact multiple of 128; buffering "one block ahead" is the usual way to get this
        // right, and hashing 128 bytes is the test that catches getting it wrong.
        var a = Blake2b.Hash(new byte[128], 64);
        var b = Blake2b.Hash(new byte[129], 64);
        Assert.NotEqual(Hex(a), Hex(b));
        Assert.Equal(64, a.Length);
    }
}
