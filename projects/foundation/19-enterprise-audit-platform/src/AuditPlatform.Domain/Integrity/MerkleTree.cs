using System.Security.Cryptography;
using System.Text;

namespace AuditPlatform.Domain.Integrity;

/// <summary>
/// Deterministic Merkle tree over an ordered batch of leaf hashes (SHA-256, hex).
///
/// Conventions (documented so a verifier written in any language can reproduce):
///   * Every hash is 32 bytes, exchanged as lowercase hex.
///   * Concatenation is byte-wise: parent = SHA256( leftBytes || rightBytes ).
///   * If a level has an odd number of nodes, the last node is duplicated to pair with itself
///     — the classic Bitcoin/RFC-6962-lite convention. Deterministic and testable.
///   * The audit path for leaf index i lists the *sibling* hash and a <see cref="MerklePathStep.IsRight"/>
///     flag indicating which side the sibling sat on when hashing. The verifier reproduces the
///     climb without needing the whole tree.
/// </summary>
public static class MerkleTree
{
    public static string ComputeRoot(IReadOnlyList<string> leafHashes)
    {
        if (leafHashes.Count == 0)
        {
            var empty = SHA256.HashData(Encoding.UTF8.GetBytes("merkle-empty"));
            return HashChain.Hex(empty);
        }
        var level = leafHashes.Select(HashChain.FromHex).ToArray();
        while (level.Length > 1)
        {
            var next = new byte[(level.Length + 1) / 2][];
            for (var i = 0; i < level.Length; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Length ? level[i + 1] : level[i];
                next[i / 2] = ParentHash(left, right);
            }
            level = next;
        }
        return HashChain.Hex(level[0]);
    }

    public static IReadOnlyList<MerklePathStep> BuildPath(IReadOnlyList<string> leafHashes, int leafIndex)
    {
        if (leafIndex < 0 || leafIndex >= leafHashes.Count) throw new ArgumentOutOfRangeException(nameof(leafIndex));
        var path = new List<MerklePathStep>();
        var level = leafHashes.Select(HashChain.FromHex).ToArray();
        var idx = leafIndex;
        while (level.Length > 1)
        {
            int siblingIdx;
            bool siblingIsRight;
            if (idx % 2 == 0)
            {
                siblingIdx = idx + 1 < level.Length ? idx + 1 : idx; // odd-tail => self-pair
                siblingIsRight = true;
            }
            else
            {
                siblingIdx = idx - 1;
                siblingIsRight = false;
            }
            path.Add(new MerklePathStep(HashChain.Hex(level[siblingIdx]), siblingIsRight));

            var next = new byte[(level.Length + 1) / 2][];
            for (var i = 0; i < level.Length; i += 2)
            {
                var left = level[i];
                var right = i + 1 < level.Length ? level[i + 1] : level[i];
                next[i / 2] = ParentHash(left, right);
            }
            level = next;
            idx /= 2;
        }
        return path;
    }

    public static bool VerifyPath(string leafHash, IReadOnlyList<MerklePathStep> path, string expectedRoot)
    {
        var current = HashChain.FromHex(leafHash);
        foreach (var step in path)
        {
            var sibling = HashChain.FromHex(step.SiblingHash);
            current = step.IsRight ? ParentHash(current, sibling) : ParentHash(sibling, current);
        }
        return string.Equals(HashChain.Hex(current), expectedRoot, StringComparison.Ordinal);
    }

    private static byte[] ParentHash(byte[] left, byte[] right)
    {
        var combined = new byte[left.Length + right.Length];
        Buffer.BlockCopy(left, 0, combined, 0, left.Length);
        Buffer.BlockCopy(right, 0, combined, left.Length, right.Length);
        return SHA256.HashData(combined);
    }
}

public sealed record MerklePathStep(string SiblingHash, bool IsRight);
