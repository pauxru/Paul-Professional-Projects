using AuditPlatform.Domain.Integrity;
using Xunit;

namespace AuditPlatform.UnitTests;

public class HashChainTests
{
    [Fact]
    public void GenesisHash_IsTenantSpecific()
    {
        var a = HashChain.GenesisHash("tenant-a");
        var b = HashChain.GenesisHash("tenant-b");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void LinkHash_IsDeterministic()
    {
        var contentHash = HashChain.ContentHash(System.Text.Encoding.UTF8.GetBytes("{\"x\":1}"));
        var prev = HashChain.GenesisHash("t");
        Assert.Equal(HashChain.LinkHash(contentHash, prev), HashChain.LinkHash(contentHash, prev));
    }

    [Fact]
    public void LinkHash_ChangesWithPayload()
    {
        var prev = HashChain.GenesisHash("t");
        var a = HashChain.LinkHash(HashChain.ContentHash(System.Text.Encoding.UTF8.GetBytes("{\"x\":1}")), prev);
        var b = HashChain.LinkHash(HashChain.ContentHash(System.Text.Encoding.UTF8.GetBytes("{\"x\":2}")), prev);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void LinkHash_ChangesWithPreviousHash()
    {
        var contentHash = HashChain.ContentHash(System.Text.Encoding.UTF8.GetBytes("{\"x\":1}"));
        var a = HashChain.LinkHash(contentHash, HashChain.GenesisHash("t"));
        var b = HashChain.LinkHash(contentHash, "0000000000000000000000000000000000000000000000000000000000000000");
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void LinkHash_SurvivesTombstoning()
    {
        // The whole point of chaining by contentHash: after we tombstone (payload wiped, only
        // the ContentHash preserved on the row), the chain link recomputes identically.
        var contentHash = HashChain.ContentHash(System.Text.Encoding.UTF8.GetBytes("{\"real\":\"payload\"}"));
        var prev = HashChain.GenesisHash("tenant");
        var chain = HashChain.LinkHash(contentHash, prev);
        // Simulate verifying a tombstoned row: same inputs (contentHash preserved), same output.
        var recomputed = HashChain.LinkHash(contentHash, prev);
        Assert.Equal(chain, recomputed);
    }

    [Fact]
    public void HexRoundTrip_Works()
    {
        var bytes = new byte[] { 0x01, 0x0A, 0xFF, 0x00 };
        var hex = HashChain.Hex(bytes);
        Assert.Equal("010aff00", hex);
        Assert.Equal(bytes, HashChain.FromHex(hex));
    }
}
