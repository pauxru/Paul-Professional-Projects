using AuditPlatform.Domain.Integrity;
using Xunit;

namespace AuditPlatform.UnitTests;

public class MerkleTreeTests
{
    private static IReadOnlyList<string> Leaves(int count)
        => Enumerable.Range(0, count)
            .Select(i => HashChain.Hex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"leaf-{i}"))))
            .ToList();

    [Fact]
    public void ComputeRoot_IsDeterministic()
    {
        var leaves = Leaves(6);
        Assert.Equal(MerkleTree.ComputeRoot(leaves), MerkleTree.ComputeRoot(leaves));
    }

    [Fact]
    public void BuildAndVerifyPath_RoundTrips_ForEveryLeaf_EvenCount()
    {
        var leaves = Leaves(8);
        var root = MerkleTree.ComputeRoot(leaves);
        for (var i = 0; i < leaves.Count; i++)
        {
            var path = MerkleTree.BuildPath(leaves, i);
            Assert.True(MerkleTree.VerifyPath(leaves[i], path, root));
        }
    }

    [Fact]
    public void BuildAndVerifyPath_RoundTrips_ForEveryLeaf_OddCount()
    {
        var leaves = Leaves(7);
        var root = MerkleTree.ComputeRoot(leaves);
        for (var i = 0; i < leaves.Count; i++)
        {
            var path = MerkleTree.BuildPath(leaves, i);
            Assert.True(MerkleTree.VerifyPath(leaves[i], path, root));
        }
    }

    [Fact]
    public void VerifyPath_RejectsForgedLeaf()
    {
        var leaves = Leaves(4);
        var root = MerkleTree.ComputeRoot(leaves);
        var path = MerkleTree.BuildPath(leaves, 1);
        var forged = HashChain.Hex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("evil")));
        Assert.False(MerkleTree.VerifyPath(forged, path, root));
    }

    [Fact]
    public void VerifyPath_RejectsWrongRoot()
    {
        var leaves = Leaves(4);
        var root = MerkleTree.ComputeRoot(leaves);
        var path = MerkleTree.BuildPath(leaves, 2);
        var wrongRoot = HashChain.Hex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("nope")));
        Assert.False(MerkleTree.VerifyPath(leaves[2], path, wrongRoot));
    }
}
