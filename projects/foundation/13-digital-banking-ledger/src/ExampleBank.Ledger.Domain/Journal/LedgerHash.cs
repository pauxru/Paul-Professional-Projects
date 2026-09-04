using System.Security.Cryptography;
using System.Text;

namespace ExampleBank.Ledger.Domain.Journal;

/// <summary>
/// Computes the tamper-evidence hash chain over journal entries. Each entry's hash is
/// SHA-256(previousHash || canonicalContent), so any retroactive edit to an entry (or to the
/// order of entries) breaks every subsequent link and is detectable by a full-chain walk.
/// </summary>
public static class LedgerHash
{
    /// <summary>The chain's genesis predecessor: 64 hex zeroes.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public static string Compute(string previousHash, string canonicalContent)
    {
        var payload = Encoding.UTF8.GetBytes(previousHash + "\n" + canonicalContent);
        var digest = SHA256.HashData(payload);
        return Convert.ToHexStringLower(digest);
    }
}
