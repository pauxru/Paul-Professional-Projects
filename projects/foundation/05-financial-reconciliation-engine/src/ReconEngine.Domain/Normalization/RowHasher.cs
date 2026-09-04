using System.Security.Cryptography;
using System.Text;

namespace ReconEngine.Domain.Normalization;

/// <summary>
/// Produces a stable, order-independent hash for a normalised record. The hash is used for
/// duplicate detection and to prove that two runs saw byte-for-byte identical inputs.
/// </summary>
public static class RowHasher
{
    /// <summary>
    /// Compute a deterministic SHA-256 hex digest over the identity-bearing fields of a record.
    /// Deliberately excludes surrogate keys and ingestion timestamps so that the same logical row
    /// always hashes to the same value regardless of when or in what order it was imported.
    /// </summary>
    public static string Hash(
        string source,
        string canonicalReference,
        long amountMinor,
        string currency,
        DateOnly valueDate,
        string status)
    {
        // Unit separator (0x1F) cannot appear in the trimmed textual fields, so the join is unambiguous.
        var canonical = string.Join('\u001F',
            source.ToUpperInvariant(),
            canonicalReference,
            amountMinor.ToString(System.Globalization.CultureInfo.InvariantCulture),
            currency.ToUpperInvariant(),
            valueDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
            status.ToUpperInvariant());

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Combine an ordered set of row hashes into a single checksum (e.g. for a run's input set).</summary>
    public static string Checksum(IEnumerable<string> rowHashes)
    {
        using var sha = SHA256.Create();
        // Sorting makes the checksum independent of enumeration order.
        foreach (var h in rowHashes.OrderBy(x => x, StringComparer.Ordinal))
        {
            var block = Encoding.UTF8.GetBytes(h);
            sha.TransformBlock(block, 0, block.Length, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash ?? []);
    }
}
